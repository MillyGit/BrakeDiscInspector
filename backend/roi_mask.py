import numpy as np
import cv2

def mask_rect(h: int, w: int, x: int, y: int, ww: int, hh: int):
    m = np.zeros((h, w), np.uint8)
    x2 = max(0, min(w, x + ww))
    y2 = max(0, min(h, y + hh))
    x = max(0, min(w, x))
    y = max(0, min(h, y))
    m[y:y2, x:x2] = 255
    return m

def mask_circle(h: int, w: int, cx: float, cy: float, r: float):
    m = np.zeros((h, w), np.uint8)
    cv2.circle(m, (int(round(cx)), int(round(cy))), int(round(r)), 255, thickness=-1, lineType=cv2.LINE_AA)
    return m

def mask_annulus(h: int, w: int, cx: float, cy: float, r: float, r_inner: float):
    outer = mask_circle(h, w, cx, cy, r)
    inner = mask_circle(h, w, cx, cy, max(0.0, r_inner))
    return cv2.subtract(outer, inner)

def build_mask(h: int, w: int, shape: dict | None):
    if not shape:
        return np.full((h, w), 255, np.uint8)
    kind = shape.get("kind", "rect").lower()
    if kind == "rect":
        return mask_rect(h, w, int(shape.get("x", 0)), int(shape.get("y", 0)),
                         int(shape.get("w", w)), int(shape.get("h", h)))
    if kind == "circle":
        return mask_circle(h, w, float(shape.get("cx", w/2)), float(shape.get("cy", h/2)),
                           float(shape.get("r", min(h, w)/2)))
    if kind == "annulus":
        return mask_annulus(h, w, float(shape.get("cx", w/2)), float(shape.get("cy", h/2)),
                            float(shape.get("r", min(h, w)/2)), float(shape.get("r_inner", 0)))
    return np.full((h, w), 255, np.uint8)
