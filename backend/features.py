from __future__ import annotations
import torch
import numpy as np
from typing import Tuple

try:
    import timm
except Exception:
    timm = None

class DinoV2Features:
    """
    DINOv2 ViT-S/14 extractor (frozen). Ajusta input al múltiplo del patch (14).
    Devuelve (embeddings: [N,D], token_hw: (H,W)).
    """
    def __init__(self, model_name: str = "vit_small_patch14_dinov2.lvd142m",
                 device: str = "auto", input_size: int = 448, patch_size: int = 14):
        if timm is None:
            raise RuntimeError("timm no está disponible. Añádelo a requirements.txt e instala dependencias.")
        self.device = self._pick_device(device)
        self.model = timm.create_model(model_name, pretrained=True)
        self.model.eval()
        for p in self.model.parameters():
            p.requires_grad_(False)
        self.model.to(self.device)
        self.patch = patch_size
        self.input_size = self._snap_to_patch(input_size, patch_size)
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

    def extract(self, img_bgr: np.ndarray) -> Tuple[np.ndarray, Tuple[int,int]]:
        x = self._preprocess(img_bgr)
        with torch.inference_mode():
            feats = self.model.forward_features(x)
            if isinstance(feats, dict):
                t = feats.get("x", None) or feats.get("tokens", None)
                if t is None:
                    t = max((v for v in feats.values() if isinstance(v, torch.Tensor)), key=lambda u: u.numel())
            else:
                t = feats
            if t.ndim == 3:
                b,n,c = t.shape
                h = w = int(n**0.5)
                if h*w != n and n>1 and int((n-1)**0.5)**2 == (n-1):
                    t = t[:,1:,:]; n -= 1; h = w = int(n**0.5)
                tok = t[0]  # (N,C)
                emb = tok.detach().cpu().numpy()
                return emb, (h,w)
            elif t.ndim == 4:
                b,c,h,w = t.shape
                tok = t[0].permute(1,2,0).reshape(h*w, c)
                emb = tok.detach().cpu().numpy()
                return emb, (h,w)
            else:
                raise RuntimeError(f"Forma inesperada de features: {t.shape}")
