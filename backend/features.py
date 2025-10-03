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
    def _prepare_input_size(self, model: nn.Module, x: torch.Tensor):
        """
        Fuerza SIEMPRE a (self.input_size, self.input_size) cuando dynamic_input=False.
        Además sincroniza patch_embed/img_size del ViT para evitar asserts.
        """
        target_h = int(self.input_size)
        target_w = int(self.input_size)
    
        # Reescalar entrada si hace falta
        H, W = x.shape[-2:]
        if (H, W) != (target_h, target_w):
            x = F.interpolate(x, size=(target_h, target_w), mode="bilinear", align_corners=False)
    
        # Sincronizar ViT para este tamaño (evita el assert de timm)
        pe = getattr(model, "patch_embed", None)
        if pe is not None:
            if hasattr(pe, "set_input_size"):
                pe.set_input_size((target_h, target_w))
            # Por si el modelo consulta estas propiedades:
            if hasattr(pe, "img_size"):
                pe.img_size = (target_h, target_w)
            if hasattr(model, "img_size"):
                model.img_size = (target_h, target_w)
    
        return x, "resize"

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

    def _normalize_layers(self, layers: list[torch.Tensor], x: torch.Tensor) -> list[torch.Tensor]:
        """
        Convierte cualquier forma a B x (Htok*Wtok) x C, quitando CLS si aparece
        y verificando que todas las capas queden con el MISMO N (= Htok*Wtok).
        """
        H, W = x.shape[-2:]
        htok, wtok = H // self.patch, W // self.patch
        expected = htok * wtok
    
        feats = []
        for i, layer in enumerate(layers):
            # Desempaquetar (tensor,) o [tensor]
            if isinstance(layer, (tuple, list)) and len(layer) > 0 and torch.is_tensor(layer[0]):
                layer = layer[0]
            if not torch.is_tensor(layer):
                raise RuntimeError(f"Tipo inesperado en capa intermedia {i}: {type(layer)}")
    
            if layer.ndim == 4:
                # (B, H, W, C) -> (B, HW, C)
                b, h, w, c = layer.shape
                layer = layer.reshape(b, h * w, c)
            elif layer.ndim == 3:
                # (B, N, C) posiblemente con CLS
                b, n, c = layer.shape
                if n == expected + 1:
                    layer = layer[:, 1:, :]  # quitar CLS
                    n = expected
            else:
                raise RuntimeError(f"Forma inesperada en capa intermedia {i}: {layer.shape}")
    
            n = layer.shape[1]
            if n != expected:
                # Mensaje explícito para detectar el desajuste (1025 vs 1370, etc.)
                raise RuntimeError(
                    f"TokenShape mismatch en capa {i}: N={n}, esperado={expected} "
                    f"(grid {htok}x{wtok}, HxW={H}x{W}, patch={self.patch})"
                )
    
            feats.append(layer)
    
        return feats

    # ---------------- tokens ----------------
    def _forward_tokens(
        self,
        x: torch.Tensor,
        *,
        use_intermediate: bool | None = None,   # None => usa intermedias si self.out_indices está definido
        want_reshape: bool = True,              # pedir (B,H,W,C) en intermedias cuando sea posible
        remove_cls: bool = True,                # quitar CLS si viene
        combine: str = "concat",                # "concat" | "mean" | "stack" (combina capas intermedias)
    ) -> torch.Tensor:
        """
        Devuelve tokens como (HW, C_out).
    
        - use_intermediate:
            True  -> intenta get_intermediate_layers (si el modelo lo soporta)
            False -> usa sólo forward_features
            None  -> usa intermedias si self.out_indices está definido, si no fallback a forward_features
        - want_reshape: cuando se usan intermedias, intenta que devuelvan (B,H,W,C)
        - remove_cls: elimina el token CLS si viene (N == HW+1)
        - combine:
            "concat" -> concatena canales de todas las capas a lo largo de C
            "mean"   -> media de canales entre capas (C_out = promedio)
            "stack"  -> apila capas en un eje extra y las aplana al final (equivalente a concat si tamaños coinciden)
        """
    
        def _expected_grid(batched_x: torch.Tensor) -> tuple[int, int, int]:
            H, W = batched_x.shape[-2:]
            htok, wtok = H // self.patch, W // self.patch
            return htok, wtok, htok * wtok
    
        def _as_BxNC(t: torch.Tensor, expected_N: int) -> torch.Tensor:
            # Convierte (B,H,W,C)->(B,HW,C), quita CLS opcional y valida N
            if t.ndim == 4:  # (B,H,W,C)
                b, h, w, c = t.shape
                t = t.reshape(b, h * w, c)
            elif t.ndim != 3:  # (B,N,C) esperado
                raise RuntimeError(f"Forma inesperada en intermedia/feature: {t.shape}")
    
            # quitar CLS si procede
            if remove_cls and t.shape[1] == expected_N + 1:
                t = t[:, 1:, :]
    
            if t.shape[1] != expected_N:
                raise RuntimeError(
                    f"TokenShape mismatch: N={t.shape[1]} esperado={expected_N} "
                    f"(patch={self.patch})"
                )
            return t
    
        # 1) Asegurar tamaño de entrada (fijo / sincronizado)
        x, _ = self._prepare_input_size(self.model, x)
        htok, wtok, expected_N = _expected_grid(x)
    
        # 2) ¿Usamos intermedias?
        if use_intermediate is None:
            use_intermediate = bool(self.out_indices) and hasattr(self.model, "get_intermediate_layers")
    
        if use_intermediate:
            try:
                layers = self._call_get_intermediate_layers_compat(
                    self.model,
                    x,
                    self.out_indices,
                    want_cls=False,
                    want_reshape=want_reshape,
                )
                if layers is not None:
                    # Desempaquetar y normalizar todas a (B,N,C)
                    normed: list[torch.Tensor] = []
                    for li, layer in enumerate(layers):
                        if isinstance(layer, (tuple, list)) and len(layer) > 0 and torch.is_tensor(layer[0]):
                            layer = layer[0]
                        if not torch.is_tensor(layer):
                            raise RuntimeError(f"Tipo inesperado en capa {li}: {type(layer)}")
                        normed.append(_as_BxNC(layer, expected_N))
    
                    # Combinar capas
                    if combine == "concat":
                        out = torch.cat(normed, dim=-1)       # (B,N, sumC)
                    elif combine == "mean":
                        # apila y promedia canales
                        stk = torch.stack(normed, dim=0)      # (L,B,N,C)
                        out = stk.mean(dim=0)                 # (B,N,C)
                    elif combine == "stack":
                        # apilar y aplanar (similar a concat si tamaños coinciden)
                        stk = torch.stack(normed, dim=-1)     # (B,N,C,L)
                        b, n, c, l = stk.shape
                        out = stk.reshape(b, n, c * l)        # (B,N,C*L)
                    else:
                        raise ValueError("combine debe ser 'concat', 'mean' o 'stack'")
    
                    return out[0]  # (N, C_out)
            except Exception as ex:
                # Fallback limpio a forward_features
                print(f"[features] fallback intermedias -> forward_features: {ex}")
    
        # 3) forward_features (fallback o seleccionado)
        feats = self.model.forward_features(x)
        if isinstance(feats, dict):
            t = feats.get("x") or feats.get("tokens")
            if t is None:
                cands = [v for v in feats.values() if isinstance(v, torch.Tensor)]
                if not cands:
                    raise RuntimeError("forward_features devolvió un dict sin tensores utilizables")
                t = max(cands, key=lambda u: u.numel())
        else:
            t = feats
    
        if t.ndim == 3:          # (B,N,C) posiblemente con CLS
            t = _as_BxNC(t, expected_N)
            return t[0]          # (N,C)
        elif t.ndim == 4:        # (B,C,H,W)
            b, c, h, w = t.shape
            return t.permute(0, 2, 3, 1).reshape(b, h * w, c)[0]  # (N,C)
        else:
            raise RuntimeError(f"Forma inesperada de features: {t.shape}")



    # ---------------- API pública ----------------
    @torch.inference_mode()
    def extract(self, img):
        x = self._preprocess(img)
        x, how = self._prepare_input_size(self.model, x)
        print(f"[features] after-prep: {x.shape[-2:]} ({how}), patch={self.patch}")
        tokens = self._forward_tokens(x)
        H, W = x.shape[-2:]
        h_tokens, w_tokens = H // self.patch, W // self.patch
        emb_np = tokens.float().detach().cpu().numpy()
        return emb_np, (int(h_tokens), int(w_tokens))

    
