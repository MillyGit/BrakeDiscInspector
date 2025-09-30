from __future__ import annotations
import torch
import numpy as np
import inspect
from typing import Tuple, Sequence

try:
    import timm
except Exception:
    timm = None

class DinoV2Features:
    """Wrapper ligero para la extracción de embeddings con DINOv2 ViT-S/14.

    - El modelo se carga congelado (`eval()` y sin gradientes).
    - La entrada se redimensiona a un tamaño múltiplo del `patch_size`.
    - Permite obtener tokens de varias capas internas (`out_indices`) y concatenarlos
      para una representación multi-escala sencilla.
    - Devuelve los embeddings L2-normalizados (N_tokens, D_concat) y la forma del
      grid de tokens (H_tokens, W_tokens).
    """

    def __init__(
        self,
        model_name: str = "vit_small_patch14_dinov2.lvd142m",
        device: str = "auto",
        input_size: int = 448,
        patch_size: int = 14,
        out_indices: Sequence[int] | None = (9, 11),
    ):
        if timm is None:
            raise RuntimeError("timm no está disponible. Añádelo a requirements.txt e instala dependencias.")
        self.device = self._pick_device(device)
        self.model = timm.create_model(model_name, pretrained=True)
        self.model.eval()
        for p in self.model.parameters():
            p.requires_grad_(False)
        self.model.to(self.device)
        self.model_name = model_name
        self.patch = patch_size
        self.input_size = self._snap_to_patch(input_size, patch_size)
        self.out_indices = tuple(sorted(out_indices)) if out_indices else None
        self.mean = torch.tensor([0.485, 0.456, 0.406], device=self.device).view(1,3,1,1)
        self.std  = torch.tensor([0.229, 0.224, 0.225], device=self.device).view(1,3,1,1)

    def _pick_device(self, d: str) -> torch.device:
        if d == "cpu":
            return torch.device("cpu")
        if d in ("cuda","auto"):
            return torch.device("cuda") if torch.cuda.is_available() else torch.device("cpu")
        return torch.device(d)

    def _snap_to_patch(self, size: int, patch: int) -> int:
        if size % patch == 0:
            return size
        low = (size // patch) * patch
        high = (size + patch - 1) // patch * patch
        return high if abs(high-size) <= abs(size-low) else (low if low>=patch else high)

    def _preprocess(self, img_bgr: np.ndarray) -> torch.Tensor:
        import cv2
        if img_bgr.ndim == 2:
            img_bgr = cv2.cvtColor(img_bgr, cv2.COLOR_GRAY2BGR)
        img = cv2.resize(img_bgr, (self.input_size, self.input_size), interpolation=cv2.INTER_LINEAR)
        img_rgb = cv2.cvtColor(img, cv2.COLOR_BGR2RGB)
        x = torch.from_numpy(img_rgb).float().permute(2,0,1).unsqueeze(0) / 255.0
        x = (x.to(self.device) - self.mean) / self.std
        return x

    def extract(self, img_bgr: np.ndarray) -> Tuple[np.ndarray, Tuple[int, int]]:
        x = self._preprocess(img_bgr)
        with torch.inference_mode():
            tokens = self._forward_tokens(x)

        emb = tokens.detach().cpu().numpy()
        emb = self._l2_normalize(emb)
        n = emb.shape[0]
        h = w = int(round(n ** 0.5))
        if h * w != n:
            raise RuntimeError(f"Forma de tokens inesperada: {n}")
        return emb, (h, w)

    # features.py (o donde hagas el forward)


    def _get_intermediate_layers_compat(model, x, n=1, want_cls=True, norm=False):
        """
        Llama a model.get_intermediate_layers con la firma que soporte el modelo.
        Soporta:
          - return_class_token
          - return_cls_token
          - sin kwargs extra (solo 'n')
        Si el modelo NO tiene get_intermediate_layers, cae a forward_features.
        """
        fn = getattr(model, "get_intermediate_layers", None)
        if fn is None:
            # Fallback: usar forward_features (timm) o forward (genérico)
            if hasattr(model, "forward_features"):
                feats = model.forward_features(x)
            else:
                feats = model(x)
    
            # Normalizamos a lista de capas para que el resto del código no cambie
            return [feats]
    
        sig = inspect.signature(fn)
        kwargs = {"n": n}
        # escoger el kw correcto según la firma disponible
        if "return_class_token" in sig.parameters:
            kwargs["return_class_token"] = want_cls
        elif "return_cls_token" in sig.parameters:
            kwargs["return_cls_token"] = want_cls
        if "norm" in sig.parameters:
            kwargs["norm"] = norm
    
        try:
            return fn(x, **kwargs)
        except TypeError:
            # último intento minimalista: solo 'n'
            return fn(x, n=n)


    def _forward_tokens(self, x: torch.Tensor) -> torch.Tensor:
        def _call_get_intermediate_layers_compat(model, x, out_indices, want_cls: bool, want_reshape: bool):
            """
            Llama a model.get_intermediate_layers con la firma compatible con la versión instalada.
            Prueba (x, out_indices, **kwargs) y, si no cuadra, (x, len(out_indices), **kwargs).
            Ajusta automáticamente: return_class_token / return_cls_token / reshape.
            Devuelve la lista de capas tal cual la API nativa (tensors o tuplas).
            """
            fn = getattr(model, "get_intermediate_layers", None)
            if fn is None:
                return None  # el caller hará forward_features
    
            sig = inspect.signature(fn)
            # kwargs compatibles con la versión presente
            kwargs = {}
            if "return_class_token" in sig.parameters:
                kwargs["return_class_token"] = want_cls
            elif "return_cls_token" in sig.parameters:
                kwargs["return_cls_token"] = want_cls
            if "reshape" in sig.parameters:
                kwargs["reshape"] = want_reshape
    
            # 1) intentar con la lista de índices tal cual (como haces ahora)
            try:
                return fn(x, self.out_indices, **kwargs)
            except TypeError:
                pass
    
            # 2) algunas versiones esperan un entero (n = últimas n capas)
            try:
                n = len(self.out_indices) if self.out_indices else 1
                return fn(x, n, **kwargs)
            except TypeError:
                pass
    
            # 3) último intento: sin kwargs (versiones antiguas)
            try:
                return fn(x, self.out_indices)
            except TypeError:
                n = len(self.out_indices) if self.out_indices else 1
                return fn(x, n)
    
        def _normalize_layers(layers):
            """
            Convierte retornos posibles en una lista de tensores Bx(HW)xC:
            - Acepta tensores 4D (B,H,W,C) o 3D (B,HW,C)
            - Si la API devuelve tuplas (tokens, cls, ...), toma el primer elemento.
            """
            feats = []
            for layer in layers:
                # Desempaquetar tuplas tipo (tokens, cls) / (feat, cls, ...)
                if isinstance(layer, (tuple, list)) and len(layer) > 0 and torch.is_tensor(layer[0]):
                    layer = layer[0]
    
                if not torch.is_tensor(layer):
                    raise RuntimeError(f"Tipo inesperado en capa intermedia: {type(layer)}")
    
                if layer.ndim == 4:
                    # (B, H, W, C)  -> (B, HW, C)
                    b, h, w, c = layer.shape
                    feats.append(layer.reshape(b, h * w, c))
                elif layer.ndim == 3:
                    # (B, HW, C)
                    feats.append(layer)
                else:
                    raise RuntimeError(f"Forma inesperada en capa intermedia: {layer.shape}")
            return feats
    
        if self.out_indices and hasattr(self.model, "get_intermediate_layers"):
            # Llamada compatible con cualquier timm/dinov2
            layers = _call_get_intermediate_layers_compat(
                self.model,
                x,
                self.out_indices,
                want_cls=False,       # conservas tu semántica original
                want_reshape=True     # lo pides si la firma lo soporta
            )
    
            if layers is None:
                # Modelo sin get_intermediate_layers -> cae al camino de abajo
                feats = self.model.forward_features(x)
                t = feats.get("x") or feats.get("tokens") if isinstance(feats, dict) else feats
                if t is None and isinstance(feats, dict):
                    t = max((v for v in feats.values() if isinstance(v, torch.Tensor)), key=lambda u: u.numel())
            else:
                # Normaliza y concatena canales como hacías
                feats = _normalize_layers(layers)
                tokens = torch.cat(feats, dim=-1)
                return tokens[0]
    
        # === Camino clásico: forward_features ===
        feats = self.model.forward_features(x)
        if isinstance(feats, dict):
            t = feats.get("x") or feats.get("tokens")
            if t is None:
                t = max((v for v in feats.values() if isinstance(v, torch.Tensor)), key=lambda u: u.numel())
        else:
            t = feats
    
        if t.ndim == 3:
            # mantener tu lógica: si incluye CLS coincide con expected, si no, drop CLS
            if t.shape[1] == self._expected_tokens(t.shape[1]):
                tokens = t
            else:
                tokens = t[:, 1:, :]
        elif t.ndim == 4:
            b, c, h, w = t.shape
            tokens = t.permute(0, 2, 3, 1).reshape(b, h * w, c)
        else:
            raise RuntimeError(f"Forma inesperada de features: {t.shape}")
    
        return tokens[0]


    def _expected_tokens(self, n: int) -> int:
        r = int(round(n ** 0.5))
        return r * r

    def _l2_normalize(self, emb: np.ndarray, eps: float = 1e-8) -> np.ndarray:
        norm = np.linalg.norm(emb, axis=1, keepdims=True) + eps
        return emb / norm
