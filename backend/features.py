import inspect
import torch
import torch.nn.functional as F
import timm
import numpy as np
from PIL import Image


class DinoV2Features:
    def __init__(self, model_name="vit_small_patch14_dinov2.lvd142m", input_size=518, out_indices=(9, 10, 11)):
        self.model_name = model_name
        self.input_size = input_size
        self.out_indices = out_indices
        self.device = torch.device("cuda" if torch.cuda.is_available() else "cpu")

        # Cargar el modelo
        self.model = timm.create_model(self.model_name, pretrained=True)
        self.model.to(self.device).eval()

        # Tamaño de patch
        pe = getattr(self.model, "patch_embed", None)
        self.patch = getattr(pe, "patch_size", 14)

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
