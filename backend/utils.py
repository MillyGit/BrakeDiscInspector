import numpy as np
import time
import json
from pathlib import Path
import base64

def now_ms() -> int:
    return int(time.time() * 1000)

def ensure_dir(p: Path):
    p.mkdir(parents=True, exist_ok=True)

def save_json(p: Path, obj: dict):
    ensure_dir(p.parent)
    p.write_text(json.dumps(obj, indent=2), encoding="utf-8")

def load_json(p: Path, default=None):
    if not p.exists():
        return default
    return json.loads(p.read_text(encoding="utf-8"))

def mm2_to_px2(area_mm2: float, mm_per_px: float) -> float:
    return float(area_mm2 / (mm_per_px ** 2))

def px2_to_mm2(area_px: float, mm_per_px: float) -> float:
    return float(area_px * (mm_per_px ** 2))

def percentile(arr: np.ndarray, p: float) -> float:
    return float(np.percentile(arr, p))

def as_b64_png(img_bgr: np.ndarray) -> str:
    import cv2
    ok, buf = cv2.imencode(".png", img_bgr)
    if not ok:
        raise RuntimeError("PNG encode failed")
    return base64.b64encode(buf.tobytes()).decode("ascii")

def base64_from_bytes(data: bytes) -> str:
    return base64.b64encode(data).decode("ascii")

