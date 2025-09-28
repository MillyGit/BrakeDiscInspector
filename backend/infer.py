from __future__ import annotations
import numpy as np
import cv2
from typing import Tuple, Optional, Dict, Any

from .features import DinoV2Features
from .patchcore import PatchCoreMemory
from .roi_mask import build_mask
from .utils import percentile, mm2_to_px2, px2_to_mm2


class InferenceEngine:
    """
    Ejecuta el pipeline de inferencia:
      ROI (BGR uint8) -> embeddings (DINOv2) -> kNN (PatchCore) -> heatmap + score
      + posproceso opcional (blur, máscara ROI, umbral, eliminación de islas, contornos).
    """
    def __init__(self,
                 extractor: DinoV2Features,
                 memory: PatchCoreMemory,
                 token_hw: Tuple[int, int],
                 mm_per_px: float,
                 k: int = 1,
                 score_percentile: int = 99):
        self.extractor = extractor
        self.memory = memory
        self.token_hw = token_hw  # (Ht, Wt) con el que se construyó la memoria
        self.mm_per_px = float(mm_per_px)
        self.k = int(k)
        self.score_p = int(score_percentile)

    def run(self,
            img_bgr: np.ndarray,
            shape: Optional[Dict[str, Any]] = None,
            blur_sigma: float = 1.0,
            area_mm2_thr: float = 1.0,
            threshold: Optional[float] = None) -> Dict[str, Any]:
        """
        Devuelve:
          {
            "score": float,
            "threshold": float (0 si no hay),
            "heatmap_u8": np.uint8[H,W]  (normalizado 0..255 y enmascarado),
            "regions": [ {"bbox":[x,y,w,h], "area_px":..., "area_mm2":...}, ... ],
            "token_shape": [Ht, Wt]
          }
        """
        # 1) Embeddings del ROI canónico
        emb, (Ht, Wt) = self.extractor.extract(img_bgr)

        # 2) Distancias kNN por parche (min-dist al coreset)
        d = self.memory.knn_min_dist(emb)  # (N,)
        heat = d.reshape(Ht, Wt)

        # 3) Reescalar a tamaño del ROI (para overlay)
        H, W = img_bgr.shape[:2]
        heat_up = cv2.resize(heat, (W, H), interpolation=cv2.INTER_LINEAR)

        # 4) Suavizado opcional
        if blur_sigma and blur_sigma > 0:
            ksize = int(max(3, round(blur_sigma * 3) * 2 + 1))
            heat_up = cv2.GaussianBlur(heat_up, (ksize, ksize), blur_sigma)

        # 5) Normalización robusta a 0..255 para visualización
        h_norm = heat_up.astype(np.float32)
        if h_norm.size > 0:
            mn, mx = np.percentile(h_norm, 1), np.percentile(h_norm, 99)
            if mx > mn:
                h_norm = (h_norm - mn) / (mx - mn)
            h_norm = np.clip(h_norm, 0.0, 1.0)
        heat_u8 = (h_norm * 255.0 + 0.5).astype(np.uint8)

        # 6) Máscara del ROI (rect/circle/annulus) si viene descrita
        mask = build_mask(H, W, shape)
        heat_u8_masked = cv2.bitwise_and(heat_u8, heat_u8, mask=mask)

        # 7) Score global (percentil sobre zona enmascarada)
        valid = heat_u8_masked[mask > 0]
        sc = percentile(valid, self.score_p) if valid.size else 0.0

        # 8) Umbral + eliminación de islas pequeñas + contornos
        regions = []
        thr = threshold if threshold is not None else 0.0
        if threshold is not None:
            _, bin_img = cv2.threshold(heat_u8_masked, int(round(threshold)), 255, cv2.THRESH_BINARY)
            # Elimina regiones con área < área mínima (en mm² → px²)
            px_thr = mm2_to_px2(area_mm2_thr, self.mm_per_px)
            cnts, _ = cv2.findContours(bin_img, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
            for c in cnts:
                area_px = cv2.contourArea(c)
                if area_px < px_thr:
                    cv2.drawContours(bin_img, [c], -1, 0, thickness=-1)
            # Recalcular contornos tras limpieza
            cnts, _ = cv2.findContours(bin_img, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
            for c in cnts:
                x, y, w, h = cv2.boundingRect(c)
                area_px = cv2.contourArea(c)
                regions.append({
                    "bbox": [int(x), int(y), int(w), int(h)],
                    "area_px": float(area_px),
                    "area_mm2": float(px2_to_mm2(area_px, self.mm_per_px)),
                })

        return {
            "score": float(sc),
            "threshold": float(thr),
            "heatmap_u8": heat_u8_masked,   # la API lo convertirá a PNG base64
            "regions": regions,
            "token_shape": [int(Ht), int(Wt)],
        }
