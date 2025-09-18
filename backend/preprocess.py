# preprocess.py
from __future__ import annotations
import os, json
from typing import Tuple, Dict, Any
import numpy as np
from PIL import Image

DEFAULT_INPUT_SIZE = (600, 600)  # (H,W)

def load_preprocessing_config(path: str) -> Dict[str, Any]:
    """
    Carga config (JSON o k=v por línea). Si no existe, devuelve defaults.
    """
    if not os.path.exists(path):
        return {"mode": "none"}
    try:
        with open(path, "r", encoding="utf-8") as f:
            txt = f.read().strip()
            if not txt:
                return {"mode": "none"}
            if txt.startswith("{"):
                return json.loads(txt)
            cfg = {}
            for line in txt.splitlines():
                if "=" in line:
                    k, v = line.split("=", 1)
                    cfg[k.strip()] = v.strip()
            return cfg or {"mode": "none"}
    except Exception:
        return {"mode": "none"}

def letterbox(img_rgb: np.ndarray, new_shape: Tuple[int,int]=(600,600), color=(0,0,0)) -> np.ndarray:
    """
    Redimensiona conservando aspecto y añade padding para llegar a new_shape.
    img_rgb: np.uint8 [H,W,3]
    """
    assert img_rgb.ndim == 3 and img_rgb.shape[2] == 3
    h, w = img_rgb.shape[:2]
    new_h, new_w = new_shape
    r = min(new_w / w, new_h / h)
    nw, nh = int(round(w * r)), int(round(h * r))

    if (nw, nh) != (w, h):
        pil = Image.fromarray(img_rgb, mode="RGB").resize((nw, nh), Image.BICUBIC)
        resized = np.array(pil, dtype=np.uint8)
    else:
        resized = img_rgb

    top = (new_h - nh) // 2
    bottom = new_h - nh - top
    left = (new_w - nw) // 2
    right = new_w - nw - left

    out = np.full((new_h, new_w, 3), color, dtype=np.uint8)
    out[top:top+nh, left:left+nw] = resized
    return out

def apply_custom_preprocessing(arr_rgb: np.ndarray, cfg: Dict[str, Any]) -> np.ndarray:
    """
    Preprocesado opcional (rápido). 'mode': none|clahe|hist_eq|gauss
    """
    import cv2
    mode = (cfg.get("mode") or "none").lower()
    if mode == "none":
        return arr_rgb.astype(np.float32)

    if mode == "hist_eq":
        yuv = cv2.cvtColor(arr_rgb, cv2.COLOR_RGB2YUV)
        yuv[:,:,0] = cv2.equalizeHist(yuv[:,:,0])
        out = cv2.cvtColor(yuv, cv2.COLOR_YUV2RGB)
        return out.astype(np.float32)

    if mode == "clahe":
        lab = cv2.cvtColor(arr_rgb, cv2.COLOR_RGB2LAB)
        l, a, b = cv2.split(lab)
        clip = float(cfg.get("clip", 2.0))
        clahe = cv2.createCLAHE(clipLimit=clip, tileGridSize=(8,8))
        l2 = clahe.apply(l)
        lab2 = cv2.merge([l2,a,b])
        out = cv2.cvtColor(lab2, cv2.COLOR_LAB2RGB)
        return out.astype(np.float32)

    if mode == "gauss":
        k = int(cfg.get("k", 3))
        k = k if k % 2 == 1 else k+1
        out = np.ascontiguousarray(arr_rgb)
        import cv2
        out = cv2.GaussianBlur(out, (k,k), 0)
        return out.astype(np.float32)

    # fallback
    return arr_rgb.astype(np.float32)

def preprocess_for_model(pil_img: Image.Image,
                         input_size: Tuple[int,int]=DEFAULT_INPUT_SIZE,
                         cfg: Dict[str, Any] | None = None) -> np.ndarray:
    """
    PIL -> RGB -> letterbox -> preproc -> normalización [-1,1] -> float32
    """
    arr = np.array(pil_img.convert("RGB"), dtype=np.uint8)
    arr = letterbox(arr, (input_size[0], input_size[1]))  # (H,W)
    arr = apply_custom_preprocessing(arr, cfg or {"mode": "none"})
    arr = arr / 127.5 - 1.0
    return arr.astype(np.float32)
