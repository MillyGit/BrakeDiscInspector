# backend/features.py
from __future__ import annotations

import io
import inspect
from typing import Iterable, Optional, Tuple, Union

import numpy as np
from PIL import Image

import torch
import torch.nn as nn
import torch.nn.functional as F
import timm


class DinoV2Features:
    """
    Extractor ViT/DINOv2 (timm) con tamaño fijo de entrada para TokenShape estable.

    extract(img) -> (embedding_numpy, (h_tokens, w_tokens))
      - pool="none" -> (HW, C)  (todos los tokens)  [recomendado para PatchCore/coreset]
      - pool="mean" -> (1,  C)  (media de tokens)

    Por defecto usamos input_size=1036 (≈ x2 de 518) con patch=14 -> 74x74 tokens.
    Se recomienda re-ejecutar fit_ok y calibrate tras cambiar input_size.
    """

    def __init__(
        self,
        model_name: str = "vit_small_patch14_dinov2.lvd142m",
        input_size: int = 1036,                 # x2 de 518 (518*2=1036) => 74x74 tokens con patch=14
        out_indices: Optional[Iterable[int]] = (9, 10, 11),
        device: Optional[Union[str, torch.device]] = None,   # "auto", "cpu", "cuda[:0]", "mps"
        half: bool = False,
        imagenet_norm: bool = True,
        pool: str = "none",                     # "none" | "mean"
        dynamic_input: bool = False,            # False => tamaño fijo; True => permite set_input_size si cuadra
        **_,
    ) -> None:
        self.model_name = model_name
        self.input_size = int(input_size)
        self.out_indices = list(out_indices) if out_indices else []
        self.dynamic_input = bool(dynamic_input)

        # --- resolver device (soporta "auto") ---
        if device is None:
            if torch.cuda.is_available():
                self.device = torch.device("cuda")
            elif getattr(torch.backends, "mps", None) and torch.backends.mps.is_available():
                self.device = torch.device("mps")
            else:
                self.device = torch.device("cpu")
        elif isinstance(device, str):
            dv = device.strip().lower()
            if dv in ("auto", ""):
                if torch.cuda.is_available():
                    self.device = torch.device("cuda")
                elif getattr(torch.backends, "mps", None) and torch.backends.mps.is_available():
                    self.device = torch.device("mps")
                else:
                    self.device = torch.device("cpu")
            else:
                self.device = torch.device(device)
        else:
            self.device = torch.device(device)

        self.half = bool(half) and self.device.type == "cuda"
        self.imagenet_norm = bool(imagenet_norm)

        # validar pool
        pool = (pool or "none").lower()
        if pool not in ("none", "mean"):
            raise ValueError("pool debe ser 'none' o 'mean'")
        self.pool = pool

        # --- modelo ---
        self.model: nn.Module = timm.create_model(self.model_name, pretrained=True)
        self.model.eval().to(self.device)
        if self.half:
            self.model.half()

        # patch size
        pe = getattr(self.model, "patch_embed", None)
        ps = getattr(pe, "patch_size", 14)
        self.patch = int(ps[0]) if isinstance(ps, (tuple, list)) else int(ps)

        # normalización tipo ImageNet
        if self.imagenet_norm:
            self.mean = torch.tensor([0.485, 0.456, 0.406], device=self.device).view(1, 3, 1, 1)
            self.std  = torch.tensor([0.229, 0.224, 0.225], device=self.device).view(1, 3, 1, 1)
        else:
            self.mean = torch.zeros((1, 3, 1, 1), device=self.device)
            self.std  = torch.ones((1, 3, 1, 1), device=self.device)

    # ---------------- imagen / preprocesado ----------------
    @staticmethod
    def _to_pil(img) -> Image.Image:
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
            if arr.shape[-1] == 3:
                # BGR (OpenCV) -> RGB
                arr = arr[..., ::-1].copy()
            return Image.fromarray(arr.astype(np.uint8), mode="RGB")
        raise TypeError(f"Tipo de imagen no soportado: {type(img)}")

    def _preprocess(self, img) -> torch.Tensor:
        pil = self._to_pil(img)
        # tamaño sugerido; _prepare_input_size fijará el tamaño exacto solicitado
        if self.input_size and self.input_size > 0:
            pil = pil.resize((self.input_size, self.input_size), Image.BICUBIC)
        arr = (np.asarray(pil).astype(np.float32) / 255.0)
        x = torch.from_numpy(arr).permute(2, 0, 1).unsqueeze(0).to(self.device)
        x = x.half() if self.half else x.float()
        x = (x - self.mean) / self.std
        return x

    # ---------------- tamaño de entrada ----------------
    def _prepare_input_size(self, model: nn.Module, x: torch.Tensor) -> Tuple[torch.Tensor, str]:
        """
        Devuelve (x_preparado, modo) con modo en {"unchanged","set_input_size","resize"}.
        - Con dynamic_input=False (por defecto): redimensiona SIEMPRE a (self.input_size, self.input_size),
          lo que garantiza TokenShape constante = (input_size / patch)^2.
        - Con dynamic_input=True: si el tamaño es múltiplo del patch y el modelo lo permite,
          actualiza el patch_embed a ese tamaño; si no, aplica resize.
        """
        target_h = int(self.input_size)
        target_w = int(self.input_size)
        H, W = x.shape[-2:]

        if (H, W) == (target_h, target_w):
            return x, "unchanged"

        if self.dynamic_input:
            pe = getattr(model, "patch_embed", None)
            ps = getattr(pe, "patch_size", getattr(self, "patch", 14))
            if isinstance(ps, int):
                ps = (ps, ps)
            if (target_h % ps[0] == 0) and (target_w % ps[1] == 0) and hasattr(pe, "set_input_size"):
                pe.set_input_size((target_h, target_w))
                if hasattr(model, "img_size"):
                    model.img_size = (target_h, target_w)
                if (H, W) != (target_h, target_w):
                    x = F.interpolate(x, size=(target_h, target_w), mode="bilinear", align_corners=False)
                return x, "set_input_size"

        # Por defecto / fallback: tamaño fijo (evita incompatibilidades de TokenShape)
        x_res = F.interpolate(x, size=(target_h, target_w), mode="bilinear", align_corners=False)
        return x_res, "resize"

    # ---------------- compat capas intermedias ----------------
    def _call_get_intermediate_layers_compat(
        self, model: nn.Module, x: torch.Tensor, out_indices: Iterable[int], want_cls: bool, want_reshape: bool
    ):
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

        out_indices = list(out_indices)
        try:
            return fn(x, out_indices, **kwargs)
        except TypeError:
            pass
        try:
            n = len(out_indices) if out_indices else 1
            return fn(x, n, **kwargs)
        except TypeError:
            pass
        try:
            return fn(x, out_indices)
        except TypeError:
            n = len(out_indices) if out_indices else 1
            return fn(x, n)

    @staticmethod
    def _normalize_layers(layers) -> list[torch.Tensor]:
        feats = []
        for layer in layers:
            if isinstance(layer, (tuple, list)) and len(layer) > 0 and torch.is_tensor(layer[0]):
                layer = layer[0]
            if not torch.is_tensor(layer):
                raise RuntimeError(f"Tipo inesperado en capa intermedia: {type(layer)}")
            if layer.ndim == 4:
                b, h, w, c = layer.shape
                feats.append(layer.reshape(b, h * w, c))
            elif layer.ndim == 3:
                feats.append(layer)
            else:
                raise RuntimeError(f"Forma inesperada en capa intermedia: {layer.shape}")
        return feats

    # ---------------- tokens ----------------
    def _forward_tokens(self, x: torch.Tensor) -> torch.Tensor:
        x, _ = self._prepare_input_size(self.model, x)

        if self.out_indices and hasattr(self.model, "get_intermediate_layers"):
            try:
                layers = self._call_get_intermediate_layers_compat(
                    self.model, x, self.out_indices, want_cls=False, want_reshape=True
                )
                if layers is not None:
                    feats = self._normalize_layers(layers)
                    tokens = torch.cat(feats, dim=-1)  # (B, HW, C_total)
                    return tokens[0]  # (HW, C)
            except Exception:
                pass

        feats = self.model.forward_features(x)
        if isinstance(feats, dict):
            t = feats.get("x") or feats.get("tokens")
            if t is None:
                candidates = [v for v in feats.values() if isinstance(v, torch.Tensor)]
                if not candidates:
                    raise RuntimeError("forward_features devolvió un dict sin tensores utilizables")
                t = max(candidates, key=lambda u: u.numel())
        else:
            t = feats

        if t.ndim == 3:
            B, N, C = t.shape
            H, W = x.shape[-2:]
            h_tokens, w_tokens = H // self.patch, W // self.patch
            expected = h_tokens * w_tokens
            if N == expected + 1:
                t = t[:, 1:, :]  # quitar CLS
            return t[0]
        elif t.ndim == 4:
            b, c, h, w = t.shape
            return t.permute(0, 2, 3, 1).reshape(b, h * w, c)[0]
        else:
            raise RuntimeError(f"Forma inesperada de features: {t.shape}")

    # ---------------- API pública ----------------
    @torch.inference_mode()
    def extract(self, img) -> Tuple[np.ndarray, Tuple[int, int]]:
        """
        Devuelve:
          emb: np.ndarray
               - pool="none" -> (HW, C)  (todos los tokens)
               - pool="mean" -> (1,  C)  (media de tokens)
          hw:  (h_tokens, w_tokens)
        """
        x = self._preprocess(img)
        x, _ = self._prepare_input_size(self.model, x)

        tokens = self._forward_tokens(x)  # (HW, C)
        H, W = x.shape[-2:]
        h_tokens, w_tokens = H // self.patch, W // self.patch

        if self.pool == "mean":
            emb_np = tokens.mean(dim=0, keepdim=True).float().detach().cpu().numpy()  # (1, C)
        else:
            emb_np = tokens.float().detach().cpu().numpy()  # (HW, C)

        return emb_np, (int(h_tokens), int(w_tokens))
