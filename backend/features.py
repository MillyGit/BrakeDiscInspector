import inspect
import torch
import torch.nn.functional as F
import timm
import numpy as np
from PIL import Image


class DinoV2Features:
import io
    def __init__(
        self,
        model_name: str = "vit_small_patch14_dinov2.lvd142m",
        input_size: int = 518,
        out_indices = (9, 10, 11),
        device=None,               # puede ser "auto", "cpu", "cuda", "cuda:0", "mps", etc.
        half: bool = False,
        imagenet_norm: bool = True,
        **_
    ):
        self.model_name = model_name
        self.input_size = int(input_size)
        self.out_indices = list(out_indices) if out_indices else []

        # --- Resolver 'device' de forma robusta ---
        resolved_device = None
        if device is None:
            # auto por defecto
            if torch.cuda.is_available():
                resolved_device = torch.device("cuda")
            elif hasattr(torch.backends, "mps") and torch.backends.mps.is_available():
                resolved_device = torch.device("mps")
            else:
                resolved_device = torch.device("cpu")
        else:
            if isinstance(device, str):
                dv = device.strip().lower()
                if dv in ("auto", ""):
                    if torch.cuda.is_available():
                        resolved_device = torch.device("cuda")
                    elif hasattr(torch.backends, "mps") and torch.backends.mps.is_available():
                        resolved_device = torch.device("mps")
                    else:
                        resolved_device = torch.device("cpu")
                else:
                    # deja que torch.device valide "cpu", "cuda", "cuda:0", "mps", etc.
                    resolved_device = torch.device(device)
            else:
                # ya es un torch.device
                resolved_device = torch.device(device)

        self.device = resolved_device

        # usar half solo si estamos en CUDA
        self.half = bool(half) and (self.device.type == "cuda")
        self.imagenet_norm = bool(imagenet_norm)

        # --- Crear modelo ---
        self.model = timm.create_model(self.model_name, pretrained=True)
        self.model.eval().to(self.device)
        if self.half:
            self.model.half()

        # Patch size
        pe = getattr(self.model, "patch_embed", None)
        ps = getattr(pe, "patch_size", 14)
        self.patch = int(ps[0]) if isinstance(ps, (tuple, list)) else int(ps)

        # Normalización tipo ImageNet
        if self.imagenet_norm:
            self.mean = torch.tensor([0.485, 0.456, 0.406], device=self.device).view(1, 3, 1, 1)
            self.std  = torch.tensor([0.229, 0.224, 0.225], device=self.device).view(1, 3, 1, 1)
        else:
            self.mean = torch.zeros((1, 3, 1, 1), device=self.device)
            self.std  = torch.ones((1, 3, 1, 1), device=self.device)


    # ---------- helpers ----------

    def _prepare_input_size(self, model, x: torch.Tensor):
        pe = getattr(model, "patch_embed", None)
        if pe is None:
            return x, "unchanged"

        H, W = x.shape[-2:]
        ps = getattr(pe, "patch_size", (self.patch, self.patch))
        if isinstance(ps, int):
            ps = (ps, ps)

        img_size = getattr(pe, "img_size", (H, W))
        if isinstance(img_size, int):
            img_size = (img_size, img_size)
        target_h, target_w = int(img_size[0]), int(img_size[1])

        if (H, W) == (target_h, target_w):
            return x, "unchanged"

        if hasattr(pe, "set_input_size") and (H % ps[0] == 0) and (W % ps[1] == 0):
            pe.set_input_size((H, W))
            if hasattr(model, "img_size"):
                model.img_size = (H, W)
            return x, "set_input_size"

        x_res = F.interpolate(x, size=(target_h, target_w), mode="bilinear", align_corners=False)
        return x_res, "resize"

    def _forward_tokens(self, x: torch.Tensor) -> torch.Tensor:
        x, _how_size = self._prepare_input_size(self.model, x)

        def _call_get_intermediate_layers_compat(model, x, out_indices, want_cls: bool, want_reshape: bool):
            fn = getattr(model, "get_intermediate_layers", None)
            if fn is None:
                return None
            sig = inspect.signature(fn)
            kwargs = {}
            if "return_class_token" in sig.parameters:
                kwargs["return_class_token"] = want_cls
            elif "return_cls_token" in sig.parameters:
                kwargs["return_cls_token"] = want_cls
            if "reshape" in sig.parameters:
                kwargs["reshape"] = want_reshape
            try:
                return fn(x, out_indices, **kwargs)
            except TypeError:
                n = len(out_indices) if out_indices else 1
                return fn(x, n, **kwargs)

        def _normalize_layers(layers):
            feats = []
            for layer in layers:
                if isinstance(layer, (tuple, list)) and len(layer) > 0 and torch.is_tensor(layer[0]):
                    layer = layer[0]
                if layer.ndim == 4:
                    b, h, w, c = layer.shape
                    feats.append(layer.reshape(b, h * w, c))
                elif layer.ndim == 3:
                    feats.append(layer)
            return feats

        if self.out_indices and hasattr(self.model, "get_intermediate_layers"):
            layers = _call_get_intermediate_layers_compat(
                self.model, x, self.out_indices, want_cls=False, want_reshape=True
            )
            if layers is not None:
                feats = _normalize_layers(layers)
                tokens = torch.cat(feats, dim=-1)
                return tokens[0]

        feats = self.model.forward_features(x)
        if isinstance(feats, dict):
            t = feats.get("x") or feats.get("tokens")
            if t is None:
                t = max((v for v in feats.values() if isinstance(v, torch.Tensor)), key=lambda u: u.numel())
        else:
            t = feats

        if t.ndim == 3:
            return t[0]
        elif t.ndim == 4:
            b, c, h, w = t.shape
            t = t.permute(0, 2, 3, 1).reshape(b, h * w, c)
            return t[0]
        else:
            raise RuntimeError(f"Forma inesperada de features: {t.shape}")

    def _to_pil(self, img) -> Image.Image:
        """Acepta PIL.Image, numpy (H,W[,3]), bytes/BytesIO o ruta a archivo, y devuelve PIL RGB."""
        if isinstance(img, Image.Image):
            return img.convert("RGB")
        if isinstance(img, (bytes, io.BytesIO)):
            return Image.open(img).convert("RGB")
        if isinstance(img, str):
            return Image.open(img).convert("RGB")
        if isinstance(img, np.ndarray):
            arr = img
            if arr.ndim == 2:
                arr = np.stack([arr] * 3, axis=-1)
            # asume BGR si viene de OpenCV → pásalo a RGB
            if arr.shape[-1] == 3:
                arr = arr[..., ::-1].copy()
            return Image.fromarray(arr.astype(np.uint8), mode="RGB")
        raise TypeError(f"Tipo de imagen no soportado: {type(img)}")
    
    def _preprocess(self, img) -> torch.Tensor:
        """Convierte a tensor (1,3,H,W) en self.device y normaliza con self.mean/std."""
        pil = self._to_pil(img)
        # Tamaño “sugerido”; _prepare_input_size ajustará exactamente lo que necesite el ViT
        if self.input_size and self.input_size > 0:
            pil = pil.resize((self.input_size, self.input_size), Image.BICUBIC)
    
        arr = (np.asarray(pil).astype(np.float32) / 255.0)  # (H,W,3) [0..1]
        x = torch.from_numpy(arr).permute(2, 0, 1).unsqueeze(0).to(self.device)  # (1,3,H,W)
        x = x.half() if self.half else x.float()
        x = (x - self.mean) / self.std
        return x
    
    @torch.inference_mode()
    def extract(self, img):
        """
        Devuelve:
          emb: np.ndarray (C,)  -> embedding promedio de tokens
          hw:  tuple(int,int)   -> (h_tokens, w_tokens) para heatmaps posteriores
        """
        x = self._preprocess(img)               # (1,3,H,W)
        x, _ = self._prepare_input_size(self.model, x)  # ajusta a 518 o usa set_input_size
    
        tokens = self._forward_tokens(x)        # (HW, C) en device
        # tamaño de la rejilla de tokens
        H, W = x.shape[-2:]
        h_tokens, w_tokens = H // self.patch, W // self.patch
    
        emb = tokens.mean(dim=0)                # (C,)
        emb_np = emb.float().detach().cpu().numpy()
        return emb_np, (int(h_tokens), int(w_tokens))
