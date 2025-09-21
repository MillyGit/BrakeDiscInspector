# app.py
import io, os, json, logging, time, base64
from pathlib import Path
from typing import Tuple, Dict, Any, Optional
import numpy as np
from PIL import Image
from flask import Flask, request, jsonify
import cv2
import tensorflow as tf

# ========= Paths =========
ROOT      = Path(__file__).resolve().parent
MODEL_DIR = ROOT / "model"
DATA_DIR  = ROOT / "data" / "train"
LOGS_DIR  = MODEL_DIR / "logs"
for d in (MODEL_DIR, DATA_DIR, LOGS_DIR):
    d.mkdir(parents=True, exist_ok=True)

MODEL_PATH = MODEL_DIR / "current_model.h5"
THR_PATH   = MODEL_DIR / "threshold.txt"
DBG_IMG    = MODEL_DIR / "last_match_debug.png"

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s | %(message)s")
log = logging.getLogger("app")

# ========= Precision (opcional) =========
try:
    tf.keras.mixed_precision.set_global_policy("mixed_float16")
except Exception:
    pass

# ========= Flask =========
app = Flask(__name__)

# ========= Preprocess (único) =========
from preprocess import preprocess_for_model, load_preprocessing_config
from status_utils import (
    file_metadata,
    tail_file,
    describe_keras_model,
    read_threshold,
)

def _load_pre_cfg_from_request() -> dict:
    """
    Lee ?config=RUTA (opcional). Si no viene, usa ./preprocessing_config.txt
    La ruta es relativa a la carpeta del backend por seguridad.
    """
    cfg_name = (request.args.get("config") or "preprocessing_config.txt").strip()
    cfg_name = cfg_name.replace("\\", "/")
    if cfg_name.startswith("/") or ".." in cfg_name:
        cfg_name = "preprocessing_config.txt"
    cfg_path = str((ROOT / cfg_name).resolve())
    if not cfg_path.startswith(str(ROOT)):
        cfg_path = str(ROOT / "preprocessing_config.txt")
    return load_preprocessing_config(cfg_path)

# ========= Modelo/umbral =========
def _ensure_model():
    if not hasattr(_ensure_model, "model"):
        if not MODEL_PATH.exists():
            return False, "Model file not found"
        _ensure_model.model = tf.keras.models.load_model(MODEL_PATH, compile=False)
    if not hasattr(_ensure_model, "thr"):
        try:
            _ensure_model.thr = float(THR_PATH.read_text().strip())
        except Exception:
            _ensure_model.thr = 0.5
    return True, "ok"

# ========= Helpers comunes (debug/PNG/num) =========
def _encode_png(img: np.ndarray) -> Optional[str]:
    """
    Codifica un ndarray (imagen) a data URL PNG base64.
    Devuelve string "data:image/png;base64,...." o None si falla.
    """
    try:
        ok, buf = cv2.imencode(".png", img)
        if not ok:
            return None
        return "data:image/png;base64," + base64.b64encode(buf.tobytes()).decode("ascii")
    except Exception:
        return None


def _generate_heatmap_b64(rgb_img: np.ndarray, mask: Optional[np.ndarray] = None) -> Optional[str]:
    """Devuelve un heatmap tipo JET codificado en base64 (sin prefijo data URI)."""
    try:
        if rgb_img.ndim != 3 or rgb_img.shape[2] != 3:
            return None
        if rgb_img.size == 0:
            return None
        arr = np.ascontiguousarray(rgb_img.astype(np.uint8))
        gray = cv2.cvtColor(arr, cv2.COLOR_RGB2GRAY)
        lap = cv2.Laplacian(gray, cv2.CV_32F, ksize=3)
        lap = np.abs(lap)
        lap = cv2.GaussianBlur(lap, (5, 5), 0)
        norm = cv2.normalize(lap, None, 0, 255, cv2.NORM_MINMAX)
        norm = norm.astype(np.uint8)
        heat = cv2.applyColorMap(norm, cv2.COLORMAP_JET)
        if mask is not None:
            mask_arr = np.array(mask, dtype=np.uint8)
            if mask_arr.ndim == 2:
                mask_u8 = np.where(mask_arr > 0, 255, 0).astype(np.uint8)
            elif mask_arr.ndim == 3:
                mask_u8 = np.where(mask_arr[..., 0] > 0, 255, 0).astype(np.uint8)
            else:
                mask_u8 = None
            if mask_u8 is not None:
                heat = cv2.bitwise_and(heat, heat, mask=mask_u8)
        ok, buf = cv2.imencode(".png", heat)
        if not ok:
            return None
        return base64.b64encode(buf.tobytes()).decode("ascii")
    except Exception:
        return None

def _finite(x, lo=None, hi=None):
    """Devuelve float finito (clip a [lo,hi]) o None si NaN/Inf/err."""
    try:
        v = float(x)
    except Exception:
        return None
    if np.isnan(v) or np.isinf(v):
        return None
    if lo is not None:
        v = max(lo, v)
    if hi is not None:
        v = min(hi, v)
    return v

def _build_debug_block(img_bgr: np.ndarray, tpl_bgr: np.ndarray, mask_tpl: Optional[np.ndarray]):
    """
    Devuelve PNGs base64:
      - gray_png: gris de la IMAGEN (sin enmascarar; coords de imagen)
      - mask_png: máscara de la PLANTILLA (coords de plantilla)
      - color_loss_png: heatmap de crominancia de la IMAGEN (sin enmascarar)
      - tpl_gray_png: gris de la PLANTILLA (enmascarado si hay máscara)
      - tpl_color_loss_png: heatmap de crominancia de la PLANTILLA (enmascarado si hay máscara)
    Y 'stats' sobre la IMAGEN (gris y color_loss).
    """
    dbg: Dict[str, Any] = {}

    # ===== IMAGEN =====
    img_g = cv2.cvtColor(img_bgr, cv2.COLOR_BGR2GRAY)
    b, g, r = cv2.split(img_bgr)
    y = img_g.astype(np.float32)
    mad = (np.abs(r.astype(np.float32) - y) +
           np.abs(g.astype(np.float32) - y) +
           np.abs(b.astype(np.float32) - y)) / 3.0
    mad = cv2.normalize(mad, None, 0, 255, cv2.NORM_MINMAX).astype(np.uint8)
    color_heat_img = cv2.applyColorMap(mad, cv2.COLORMAP_JET)

    dbg["gray_png"]       = _encode_png(img_g)
    dbg["color_loss_png"] = _encode_png(color_heat_img)

    # ===== PLANTILLA =====
    tpl_g = cv2.cvtColor(tpl_bgr, cv2.COLOR_BGR2GRAY)
    b2, g2, r2 = cv2.split(tpl_bgr)
    y2 = tpl_g.astype(np.float32)
    mad2 = (np.abs(r2.astype(np.float32) - y2) +
            np.abs(g2.astype(np.float32) - y2) +
            np.abs(b2.astype(np.float32) - y2)) / 3.0
    mad2 = cv2.normalize(mad2, None, 0, 255, cv2.NORM_MINMAX).astype(np.uint8)
    color_heat_tpl = cv2.applyColorMap(mad2, cv2.COLORMAP_JET)

    if mask_tpl is not None:
        m3 = cv2.merge([mask_tpl, mask_tpl, mask_tpl])
        color_heat_tpl = np.where(m3 > 0, color_heat_tpl, 0)
        tpl_g_vis = np.where(mask_tpl > 0, tpl_g, 0)
    else:
        tpl_g_vis = tpl_g

    dbg["mask_png"]            = _encode_png(mask_tpl) if mask_tpl is not None else None
    dbg["tpl_gray_png"]        = _encode_png(tpl_g_vis)
    dbg["tpl_color_loss_png"]  = _encode_png(color_heat_tpl)

    stats = {
        "gray_mean": float(np.mean(img_g)),
        "gray_std": float(np.std(img_g)),
        "color_loss_mean": float(np.mean(mad)),
        "color_loss_std": float(np.std(mad)),
        "mask_coverage": (float((mask_tpl > 0).sum()) / float(mask_tpl.size) if mask_tpl is not None else None)
    }
    dbg["stats"] = stats
    return dbg

def _draw_quad_dbg(img_bgr, quad_pts, center, color):
    dbg = img_bgr.copy()
    pts = np.int32(quad_pts).reshape(-1, 1, 2)
    cv2.polylines(dbg, [pts], True, color, 2)
    cv2.drawMarker(
        dbg, (int(round(center[0])), int(round(center[1]))),
        color, markerType=cv2.MARKER_CROSS, markerSize=20, thickness=2
    )
    try:
        cv2.imwrite(str(DBG_IMG), dbg)
    except Exception:
        pass

def _build_mask_from_tpl(bgr, alpha):
    if alpha is not None:
        m = (alpha > 0).astype(np.uint8) * 255
    else:
        g = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY)
        zeros = int((g == 0).sum())
        if zeros > 0.05 * g.size:
            _, m = cv2.threshold(g, 0, 255, cv2.THRESH_BINARY)
        else:
            m = None
    if m is not None:
        k = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (3, 3))
        m = cv2.morphologyEx(m, cv2.MORPH_ERODE, k, iterations=1)
    return m

def _match_template(img_gray, tpl_gray, mask=None):
    # Si hay máscara, intentamos TM_CCORR_NORMED con mask (OpenCV >=4.3)
    if mask is not None:
        try:
            return cv2.matchTemplate(img_gray, tpl_gray, cv2.TM_CCORR_NORMED, mask=mask), cv2.TM_CCORR_NORMED
        except TypeError:
            pass
    return cv2.matchTemplate(img_gray, tpl_gray, cv2.TM_CCOEFF_NORMED), cv2.TM_CCOEFF_NORMED

# ========= Endpoints =========
@app.route("/predict", methods=["POST"])
def predict():
    ok, msg = _ensure_model()
    if not ok:
        return jsonify({"error": f"model not loaded: {msg}"}), 503

    files = request.files.getlist("image")
    if not files:
        return jsonify({"error": "no image files"}), 400

    PRE_CFG = _load_pre_cfg_from_request()

    arrs, names = [], []
    for f in files:
        pil = Image.open(io.BytesIO(f.read()))
        arrs.append(preprocess_for_model(pil, (600,600), PRE_CFG))
        names.append(f.filename or "img.png")

    batch = np.stack(arrs, axis=0)
    preds = _ensure_model.model.predict(batch, verbose=0).reshape((-1,))
    thr = _ensure_model.thr

    out = []
    for name, p in zip(names, preds):
        status = "good" if p >= thr else "defective"
        out.append({"filename": name, "confidence": float(p), "status": status})
    return jsonify(out)

@app.route("/upload_roi", methods=["POST"])
def upload_roi():
    f = request.files.get("image")
    label = (request.form.get("label") or "").strip().lower()
    if label not in {"good", "defective"}:
        return jsonify({"error": "label must be 'good' or 'defective'"}), 400
    if not f:
        return jsonify({"error": "no image file"}), 400

    sub = DATA_DIR / label
    sub.mkdir(parents=True, exist_ok=True)
    name = f.filename or f"roi_{int(time.time())}.png"
    (sub / name).write_bytes(f.read())
    return jsonify({"ok": True, "saved": str(sub / name)})

@app.post("/match_one")
@app.route("/match_master", methods=["POST"])
def match_master():
    """
    Matching Master con modos: geom | tm_rot | sift/orb/auto (fallbacks incluidos)
    DEBUG opcional (debug=1) devuelve bloques de visualización (base64).
    Si llegan search_x/y/w/h, recorta la imagen a esa zona antes de buscar (acelera muchísimo).
    """
    t0_all = time.time()

    def _safe_float(x, default):
        try:
            v = float(x)
            if np.isnan(v) or np.isinf(v):
                return default
            return v
        except Exception:
            return default

    def _create_feature(detector: str):
        det = (detector or "auto").lower()
        if det in ("sift", "auto"):
            sift = getattr(cv2, "SIFT_create", None)
            if sift is not None:
                return sift(), ("sift" if det == "sift" else "auto->sift")
            if det == "sift":
                raise RuntimeError("SIFT not available (opencv-contrib ausente)")
        # ORB por defecto
        return (
            cv2.ORB_create(nfeatures=2000, scaleFactor=1.2, nlevels=8, edgeThreshold=31, patchSize=31),
            ("orb" if det == "orb" else "auto->orb"),
        )

    def _match_template(img_gray, tpl_gray, mask=None):
        if mask is not None:
            try:
                return cv2.matchTemplate(img_gray, tpl_gray, cv2.TM_CCORR_NORMED, mask=mask), cv2.TM_CCORR_NORMED
            except TypeError:
                pass
        return cv2.matchTemplate(img_gray, tpl_gray, cv2.TM_CCOEFF_NORMED), cv2.TM_CCOEFF_NORMED

    # ---------- inputs ----------
    file_img = request.files.get("image")
    file_tpl = request.files.get("template")
    if not file_img or not file_tpl:
        return jsonify({"found": False, "reason": "missing_files"}), 400

    # alias robustos
    thr     = _safe_float(request.form.get("thr"), 0.6)
    rot_rg  = _safe_float(request.form.get("rot_range") or request.form.get("rot_rg"), 25.0)
    smin    = _safe_float(request.form.get("scale_min") or request.form.get("smin"), 0.8)
    smax    = _safe_float(request.form.get("scale_max") or request.form.get("smax"), 1.2)
    tm_thr  = _safe_float(request.form.get("tm_thr"), 0.8)
    feature = (request.form.get("feature") or "auto").lower()  # auto|sift|orb|tm_rot|geom
    dbg_req = request.form.get("debug") in ("1", "true", "True")

    # ---------- decodificación ----------
    t0_decode = time.time()
    img_np = cv2.imdecode(np.frombuffer(file_img.read(), np.uint8), cv2.IMREAD_COLOR)
    if img_np is None:
        return jsonify({"found": False, "reason": "decode_error_img"}), 400

    tpl_raw = cv2.imdecode(np.frombuffer(file_tpl.read(), np.uint8), cv2.IMREAD_UNCHANGED)
    if tpl_raw is None:
        return jsonify({"found": False, "reason": "decode_error_tpl"}), 400
    t1_decode = time.time()

    # template BGR + alpha
    if tpl_raw.ndim == 3 and tpl_raw.shape[2] == 4:
        tpl_bgr = tpl_raw[:, :, :3]
        alpha   = tpl_raw[:, :, 3]
    else:
        tpl_bgr = tpl_raw if tpl_raw.ndim == 3 else cv2.cvtColor(tpl_raw, cv2.COLOR_GRAY2BGR)
        alpha   = None

    # máscara desde la plantilla
    def _build_mask_from_tpl(bgr, alpha):
        if alpha is not None:
            m = (alpha > 0).astype(np.uint8) * 255
        else:
            g = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY)
            zeros = int((g == 0).sum())
            if zeros > 0.05 * g.size:
                _, m = cv2.threshold(g, 0, 255, cv2.THRESH_BINARY)
            else:
                m = None
        if m is not None:
            k = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (3, 3))
            m = cv2.morphologyEx(m, cv2.MORPH_ERODE, k, iterations=1)
        return m

    mask_tpl = _build_mask_from_tpl(tpl_bgr, alpha)

    # ---------- SEARCH ROI opcional ----------
    # Si llegan search_x/y/w/h, recortamos img_np y guardamos offsets para devolver coords absolutas
    sx = request.form.get("search_x"); sy = request.form.get("search_y")
    sw = request.form.get("search_w"); sh = request.form.get("search_h")
    off_x = 0; off_y = 0
    if all(v is not None and str(v).strip() != "" for v in (sx, sy, sw, sh)):
        try:
            x = max(0, int(float(sx))); y = max(0, int(float(sy)))
            w = max(1, int(float(sw))); h = max(1, int(float(sh)))
            H, W = img_np.shape[:2]
            x2 = min(W, x + w); y2 = min(H, y + h)
            if x < x2 and y < y2:
                img_np = img_np[y:y2, x:x2].copy()
                off_x, off_y = x, y
        except Exception:
            pass  # si fallan valores, seguimos sin recorte

    img_g = cv2.cvtColor(img_np, cv2.COLOR_BGR2GRAY)
    tpl_g = cv2.cvtColor(tpl_bgr, cv2.COLOR_BGR2GRAY)

    # ---------- bloque debug (opcional, igual que tu versión) ----------
    def _encode_png(img):
        try:
            ok, buf = cv2.imencode(".png", img)
            if not ok: return None
            return "data:image/png;base64," + base64.b64encode(buf.tobytes()).decode("ascii")
        except Exception:
            return None

    def _build_debug_block(img_bgr, tpl_bgr, mask_tpl):
        dbg = {}
        img_gray = cv2.cvtColor(img_bgr, cv2.COLOR_BGR2GRAY)
        b, g, r = cv2.split(img_bgr)
        y = img_gray.astype(np.float32)
        mad = (np.abs(r.astype(np.float32) - y) +
               np.abs(g.astype(np.float32) - y) +
               np.abs(b.astype(np.float32) - y)) / 3.0
        mad = cv2.normalize(mad, None, 0, 255, cv2.NORM_MINMAX).astype(np.uint8)
        color_heat_img = cv2.applyColorMap(mad, cv2.COLORMAP_JET)

        dbg["gray_png"]       = _encode_png(img_gray)
        dbg["color_loss_png"] = _encode_png(color_heat_img)

        tpl_g2 = cv2.cvtColor(tpl_bgr, cv2.COLOR_BGR2GRAY)
        b2, g2, r2 = cv2.split(tpl_bgr)
        y2 = tpl_g2.astype(np.float32)
        mad2 = (np.abs(r2.astype(np.float32) - y2) +
                np.abs(g2.astype(np.float32) - y2) +
                np.abs(b2.astype(np.float32) - y2)) / 3.0
        mad2 = cv2.normalize(mad2, None, 0, 255, cv2.NORM_MINMAX).astype(np.uint8)
        color_heat_tpl = cv2.applyColorMap(mad2, cv2.COLORMAP_JET)
        if mask_tpl is not None:
            m3 = cv2.merge([mask_tpl, mask_tpl, mask_tpl])
            color_heat_tpl = np.where(m3 > 0, color_heat_tpl, 0)
            tpl_g_vis = np.where(mask_tpl > 0, tpl_g2, 0)
        else:
            tpl_g_vis = tpl_g2

        dbg["mask_png"]           = _encode_png(mask_tpl) if mask_tpl is not None else None
        dbg["tpl_gray_png"]       = _encode_png(tpl_g_vis)
        dbg["tpl_color_loss_png"] = _encode_png(color_heat_tpl)
        return dbg

    debug_block = _build_debug_block(img_np, tpl_bgr, mask_tpl) if dbg_req else None

    # Helper para ajustar salida a coordenadas absolutas de la imagen original
    def _resp_center(cx, cy, payload):
        cx_abs = float(cx + off_x)
        cy_abs = float(cy + off_y)
        payload.update({"center_x": cx_abs, "center_y": cy_abs})
        if debug_block: payload["debug"] = debug_block
        return jsonify(payload)

    # ---------- GEOM ----------
    def match_geom(img_g, tpl_g):
        def find_quad(gray):
            e = cv2.Canny(gray, 60, 180)
            cnts, _ = cv2.findContours(e, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
            if not cnts: return None
            cnt = max(cnts, key=cv2.contourArea)
            peri = cv2.arcLength(cnt, True)
            poly = cv2.approxPolyDP(cnt, 0.02 * peri, True)
            if len(poly) < 4:
                rect = cv2.minAreaRect(cnt)
                box = cv2.boxPoints(rect)
                return box
            if len(poly) > 4:
                hull = cv2.convexHull(poly)
                if len(hull) >= 4:
                    return hull[:4, 0, :]
                return poly[:4, 0, :]
            return poly[:, 0, :]

        q_tpl = find_quad(tpl_g)
        q_img = find_quad(img_g)
        if q_tpl is None or q_img is None:
            return None

        def order_pts(pts):
            pts = np.array(pts, dtype=np.float32)
            c = pts.mean(axis=0)
            ang = np.arctan2(pts[:, 1] - c[1], pts[:, 0] - c[0])
            idx = np.argsort(ang)
            return pts[idx]

        q_tpl = order_pts(q_tpl)
        q_img = order_pts(q_img)

        H, mask = cv2.findHomography(q_tpl.reshape(-1, 1, 2), q_img.reshape(-1, 1, 2), cv2.RANSAC, 5.0)
        if H is None:
            return None

        center = q_img.mean(axis=0)
        return {"center": center.tolist(), "H": H, "conf": 1.0}

    # ---------- TM_ROT ----------
    def match_tm_rot(img_g, tpl_g, mask_tpl, rot_deg, smin, smax):
        step_deg = max(1.0, min(5.0, rot_deg / 10.0))
        scales = [1.0]
        if abs(smax - smin) > 1e-3:
            steps = max(2, int(3 * ((smax - smin) / 0.2)))
            scales = np.linspace(smin, smax, steps).tolist()

        best = -1.0; best_xy = (0, 0); best_angle = 0.0; best_scale = 1.0
        best_w = tpl_g.shape[1]; best_h = tpl_g.shape[0]; best_method = None

        h_t, w_t = tpl_g.shape[:2]
        for s in scales:
            w_s = max(1, int(w_t * s)); h_s = max(1, int(h_t * s))
            tpl_s = cv2.resize(tpl_g, (w_s, h_s), interpolation=cv2.INTER_CUBIC)
            mask_s = cv2.resize(mask_tpl, (w_s, h_s), interpolation=cv2.INTER_NEAREST) if mask_tpl is not None else None
            for a in np.arange(-rot_deg, rot_deg + 1e-3, step_deg):
                M = cv2.getRotationMatrix2D((w_s / 2.0, h_s / 2.0), a, 1.0)
                tpl_r = cv2.warpAffine(tpl_s, M, (w_s, h_s), flags=cv2.INTER_CUBIC, borderMode=cv2.BORDER_REPLICATE)
                mask_r = None
                if mask_s is not None:
                    mask_r = cv2.warpAffine(mask_s, M, (w_s, h_s), flags=cv2.INTER_NEAREST,
                                            borderMode=cv2.BORDER_CONSTANT, borderValue=0)

                if img_g.shape[0] < h_s or img_g.shape[1] < w_s:
                    continue

                if mask_r is not None:
                    try:
                        res = cv2.matchTemplate(img_g, tpl_r, cv2.TM_CCORR_NORMED, mask=mask_r)
                        method_name = "CCORR_NORMED(mask)"
                    except TypeError:
                        res = cv2.matchTemplate(img_g, tpl_r, cv2.TM_CCOEFF_NORMED)
                        method_name = "CCOEFF_NORMED"
                else:
                    res = cv2.matchTemplate(img_g, tpl_r, cv2.TM_CCOEFF_NORMED)
                    method_name = "CCOEFF_NORMED"

                _, maxVal, _, maxLoc = cv2.minMaxLoc(res)
                if maxVal > best:
                    best = float(maxVal); best_xy = maxLoc
                    best_angle = float(a); best_scale = float(s)
                    best_w, best_h = w_s, h_s; best_method = method_name

        best = float(np.clip(best, -1.0, 0.999999))
        return {
            "tm_best": best,
            "angle_deg": best_angle, "scale": best_scale,
            "top_left": [float(best_xy[0]), float(best_xy[1])],
            "tpl_w": int(best_w), "tpl_h": int(best_h),
            "used_mask": mask_tpl is not None, "method": best_method,
        }

    # ========= LÓGICA PRINCIPAL =========
    t0_proc = time.time()

    if feature == "geom":
        out = match_geom(img_g, tpl_g)
        if out is not None:
            cx, cy = out["center"]
            t1 = time.time()
            log.info(f"[GEOM_OK] time_decode={t1_decode-t0_decode:.3f}s crop={off_x},{off_y} proc={t1-t0_proc:.3f}s total={t1-t0_all:.3f}s")
            return _resp_center(cx, cy, {
                "found": True, "stage": "GEOM_OK",
                "confidence": out["conf"],
                "tm_best": None, "tm_thr": tm_thr,
                "sift_orb": {"detector": "geom"},
            })
        feature = "tm_rot"  # fallback

    if feature == "tm_rot":
        rot_out = match_tm_rot(img_g, tpl_g, mask_tpl, rot_rg, smin, smax)
        best = rot_out["tm_best"]
        if best >= tm_thr:
            x, y = map(int, rot_out["top_left"])
            w_r = int(rot_out.get("tpl_w") or tpl_g.shape[1])
            h_r = int(rot_out.get("tpl_h") or tpl_g.shape[0])
            cx, cy = x + w_r / 2.0, y + h_r / 2.0
            try:
                dbg = img_np.copy()
                cv2.rectangle(dbg, (x, y), (x + w_r, y + h_r), (0, 255, 255), 2)
                cv2.drawMarker(dbg, (int(round(cx)), int(round(cy))),
                               (0, 255, 255), markerType=cv2.MARKER_CROSS, markerSize=20, thickness=2)
                cv2.imwrite(str(DBG_IMG), dbg)
            except Exception:
                pass
            t1 = time.time()
            log.info(f"[TM_ROT_OK] time_decode={t1_decode-t0_decode:.3f}s crop={off_x},{off_y} proc={t1-t0_proc:.3f}s total={t1-t0_all:.3f}s")
            return _resp_center(cx, cy, {
                "found": True, "stage": "TM_ROT_OK",
                "confidence": float(best),
                "tm_best": float(best), "tm_thr": float(tm_thr),
                "tm_rot": rot_out
            })
        else:
            t1 = time.time()
            log.info(f"[TM_ROT_FAIL] best={best:.3f} thr={tm_thr:.3f} decode={t1_decode-t0_decode:.3f}s proc={t1-t0_proc:.3f}s total={t1-t0_all:.3f}s")
            return jsonify({
                "found": False, "stage": "TM_ROT_FAIL",
                "reason": "tm_rot_below_threshold",
                "tm_best": float(best), "tm_thr": float(tm_thr),
                "tm_rot": rot_out, "crop_off": [off_x, off_y]
            })

    # ----- SIFT/ORB -----
    sift_orb_metrics = {
        "detector": None, "kp_tpl": 0, "kp_img": 0, "matches": 0, "good": 0,
        "inliers": 0, "confidence": None, "avg_distance": None, "reproj_rmse": None,
        "scale_est": None, "angle_est": None,
    }
    try:
        det, det_name = _create_feature(feature)
        sift_orb_metrics["detector"] = det_name
        kps1, des1 = det.detectAndCompute(tpl_g, None)
        kps2, des2 = det.detectAndCompute(img_g, None)
        if des1 is None or des2 is None or not kps1 or not kps2:
            raise RuntimeError("not_enough_keypoints")

        if det_name.endswith("orb"):
            bf = cv2.BFMatcher(cv2.NORM_HAMMING, crossCheck=False)
        else:
            bf = cv2.BFMatcher(cv2.NORM_L2, crossCheck=False)
        knn = bf.knnMatch(des1, des2, k=2)

        good, dists = [], []
        for pair in knn or []:
            if len(pair) < 2: continue
            m, n = pair
            if m.distance < (0.75 * n.distance):
                good.append(m); dists.append(float(m.distance))
        if len(good) < 4:
            raise RuntimeError("not_enough_good_matches")

        src_pts = np.float32([kps1[m.queryIdx].pt for m in good]).reshape(-1, 1, 2)
        dst_pts = np.float32([kps2[m.trainIdx].pt for m in good]).reshape(-1, 1, 2)

        A, mask_inl = cv2.estimateAffinePartial2D(
            src_pts, dst_pts, method=cv2.RANSAC, ransacReprojThreshold=5.0, maxIters=2000, confidence=0.99, refineIters=50
        )
        if A is None or mask_inl is None:
            raise RuntimeError("no_affine")

        inliers = int(mask_inl.sum())
        sift_orb_metrics["inliers"] = inliers
        conf = inliers / max(1, len(good))
        sift_orb_metrics["confidence"] = float(conf)

        h_t, w_t = tpl_g.shape[:2]
        corners = np.float32([[0, 0], [w_t, 0], [w_t, h_t], [0, h_t]]).reshape(-1, 1, 2)
        proj_quad = cv2.transform(corners, A).reshape(-1, 2)
        cx0, cy0 = float(np.mean(proj_quad[:, 0])), float(np.mean(proj_quad[:, 1]))

        try:
            dbg = img_np.copy()
            pts = np.int32(proj_quad).reshape(-1, 1, 2)
            cv2.polylines(dbg, [pts], True, (0, 255, 0), 2)
            cv2.drawMarker(dbg, (int(round(cx0)), int(round(cy0))),
                           (0, 255, 0), markerType=cv2.MARKER_CROSS, markerSize=20, thickness=2)
            cv2.imwrite(str(DBG_IMG), dbg)
        except Exception:
            pass

        t1 = time.time()
        log.info(f"[H_OK] decode={t1_decode-t0_decode:.3f}s crop={off_x},{off_y} proc={t1-t0_proc:.3f}s total={t1-t0_all:.3f}s")
        return _resp_center(cx0, cy0, {
            "found": True, "stage": "H_OK",
            "confidence": float(conf),
            "angle_deg": float(np.degrees(np.arctan2(A[1,0], A[0,0]))),
            "scale": float(np.sqrt(max(0.0, (A[0,0]**2 + A[0,1]**2 + A[1,0]**2 + A[1,1]**2) / 2.0))),
            "tm_best": None, "tm_thr": tm_thr,
            "sift_orb": sift_orb_metrics,
        })
    except Exception as e:
        sift_orb_metrics["fail_reason"] = str(e)

    # ----- fallback TM normal -----
    res, method = _match_template(img_g, tpl_g, mask_tpl)
    _, maxVal, _, maxLoc = cv2.minMaxLoc(res)
    best = float(np.clip(maxVal, -1.0, 0.999999))
    if best >= tm_thr:
        h_t, w_t = tpl_g.shape[:2]
        x, y = maxLoc
        cx, cy = x + w_t / 2.0, y + h_t / 2.0

        try:
            dbg = img_np.copy()
            cv2.rectangle(dbg, (x, y), (x + w_t, y + h_t), (0, 255, 255), 2)
            cv2.drawMarker(dbg, (int(round(cx)), int(round(cy))),
                           (0, 255, 255), markerType=cv2.MARKER_CROSS, markerSize=20, thickness=2)
            cv2.imwrite(str(DBG_IMG), dbg)
        except Exception:
            pass

        t1 = time.time()
        log.info(f"[TM_OK] decode={t1_decode-t0_decode:.3f}s crop={off_x},{off_y} proc={t1-t0_proc:.3f}s total={t1-t0_all:.3f}s")
        return _resp_center(cx, cy, {
            "found": True, "stage": "TM_OK",
            "confidence": float(best),
            "bbox": [float(x + off_x), float(y + off_y), float(w_t), float(h_t)],
            "tm_best": float(best), "tm_thr": float(tm_thr),
            "method": int(method),
            "sift_orb": sift_orb_metrics,
        })

    t1 = time.time()
    log.info(f"[TM_FAIL] best={best:.3f} thr={tm_thr:.3f} decode={t1_decode-t0_decode:.3f}s crop={off_x},{off_y} proc={t1-t0_proc:.3f}s total={t1-t0_all:.3f}s")
    resp = {
        "found": False, "stage": "TM_FAIL",
        "reason": "tm_below_threshold",
        "tm_best": float(best), "tm_thr": float(tm_thr),
        "sift_orb": sift_orb_metrics,
        "crop_off": [off_x, off_y],
    }
    if debug_block: resp["debug"] = debug_block
    return jsonify(resp)


    
@app.post("/analyze")
def analyze():
    ok, msg = _ensure_model()
    if not ok:
        return jsonify({"error": f"model not loaded: {msg}"}), 503

    file_storage = request.files.get("file")
    if file_storage is None:
        return jsonify({"error": "missing file"}), 400

    data = file_storage.read()
    if not data:
        return jsonify({"error": "empty file"}), 400

    try:
        pil_src = Image.open(io.BytesIO(data))
    except Exception:
        return jsonify({"error": "invalid image"}), 400

    try:
        rgba = np.array(pil_src.convert("RGBA"), dtype=np.uint8)
    except Exception:
        return jsonify({"error": "invalid image"}), 400

    if rgba.ndim != 3 or rgba.shape[2] < 3:
        return jsonify({"error": "invalid image"}), 400

    rgb = rgba[..., :3]
    if rgb.size == 0:
        return jsonify({"error": "empty image"}), 400

    mask_bool: Optional[np.ndarray] = None
    if rgba.shape[2] == 4:
        alpha_mask = rgba[..., 3] > 0
        if np.any(~alpha_mask):
            mask_bool = alpha_mask

    mask_file = request.files.get("mask")
    if mask_file and mask_file.filename:
        mask_bytes = mask_file.read()
        if not mask_bytes:
            return jsonify({"error": "empty mask"}), 400
        try:
            mask_img = Image.open(io.BytesIO(mask_bytes)).convert("L")
            mask_arr = np.array(mask_img, dtype=np.uint8)
        except Exception:
            return jsonify({"error": "invalid mask"}), 400
        if mask_arr.shape != rgb.shape[:2]:
            return jsonify({"error": "mask size mismatch"}), 400
        mask_from_file = mask_arr > 0
        mask_bool = mask_from_file if mask_bool is None else (mask_bool & mask_from_file)
    else:
        annulus_raw = request.form.get("annulus")
        if annulus_raw:
            try:
                annulus_json = json.loads(annulus_raw)
                cx = float(annulus_json["cx"])
                cy = float(annulus_json["cy"])
                ro = float(annulus_json["ro"])
                ri_raw = annulus_json.get("ri", 0.0)
                ri = float(ri_raw) if ri_raw is not None else 0.0
            except (KeyError, TypeError, ValueError, json.JSONDecodeError):
                return jsonify({"error": "invalid annulus"}), 400
            if not np.isfinite(cx) or not np.isfinite(cy) or not np.isfinite(ro) or not np.isfinite(ri):
                return jsonify({"error": "invalid annulus"}), 400
            if ro <= 0 or ro <= ri:
                return jsonify({"error": "invalid annulus"}), 400
            h, w = rgb.shape[:2]
            yy, xx = np.ogrid[:h, :w]
            dist2 = (xx - cx) ** 2 + (yy - cy) ** 2
            ann_mask = dist2 <= (ro ** 2)
            if ri > 0:
                ann_mask &= dist2 >= (ri ** 2)
            mask_bool = ann_mask if mask_bool is None else (mask_bool & ann_mask)

    rgb_for_model = rgb.copy()
    if mask_bool is not None:
        rgb_for_model[~mask_bool] = 0

    try:
        pre_cfg = _load_pre_cfg_from_request()
        arr = preprocess_for_model(Image.fromarray(rgb_for_model), (600, 600), pre_cfg)
        batch = np.expand_dims(arr, axis=0)
        preds = _ensure_model.model.predict(batch, verbose=0).reshape((-1,))
        score_raw = float(preds[0]) if preds.size else 0.0
        score = _finite(score_raw, lo=0.0, hi=1.0)
        if score is None:
            raise ValueError("invalid score")
        threshold = getattr(_ensure_model, "thr", 0.5)
        thr = _finite(threshold, lo=0.0, hi=1.0)
        if thr is None:
            thr = 0.5
        label = "NG" if score >= thr else "OK"
        heatmap_b64 = _generate_heatmap_b64(rgb_for_model, mask_bool)
        if heatmap_b64 is None:
            blank = np.zeros((rgb_for_model.shape[0], rgb_for_model.shape[1], 3), dtype=np.uint8)
            ok_png, buf = cv2.imencode(".png", blank)
            heatmap_b64 = base64.b64encode(buf.tobytes()).decode("ascii") if ok_png else ""
        return jsonify({
            "label": label,
            "score": float(score),
            "threshold": float(thr),
            "heatmap_png_b64": heatmap_b64,
        })
    except json.JSONDecodeError:
        return jsonify({"error": "invalid annulus"}), 400
    except Exception as exc:
        log.exception("/analyze failed: %s", exc)
        return jsonify({"error": "inference failed"}), 500


# ========= /train_status =========
@app.route("/train_status", methods=["GET"])
def train_status():
    """
    Devuelve estado del entrenamiento si existe:
      - model/train_status.json (si tu loop de train lo va escribiendo)
      - model/logs/train.log (tail)
      - model/train_pid.txt (pid)
    """
    status = {"state": "idle"}
    st_json = MODEL_DIR / "train_status.json"
    pid_file = MODEL_DIR / "train_pid.txt"
    log_file = LOGS_DIR / "train.log"

    artifacts = {
        "model": file_metadata(MODEL_PATH),
        "threshold": file_metadata(THR_PATH),
        "train_status": file_metadata(st_json),
        "pid_file": file_metadata(pid_file),
        "log": file_metadata(log_file),
    }

    if st_json.exists():
        try:
            data = json.loads(st_json.read_text())
            if isinstance(data, dict):
                status.update(data)
        except Exception:
            pass

    status["artifacts"] = artifacts

    if pid_file.exists():
        try:
            pid = int(pid_file.read_text().strip())
            status["pid"] = pid
            artifacts["pid_file"]["pid"] = pid
        except Exception:
            status["pid"] = None

    log_tail = tail_file(log_file, max_bytes=4000)
    if log_tail is not None:
        status["log_tail"] = log_tail
        artifacts["log"]["tail"] = log_tail
        if "updated_at" not in status:
            status["updated_at"] = time.time()

    model_ok, model_msg = _ensure_model()
    if model_ok:
        artifacts["model"]["loaded"] = True
        status["model_runtime"] = describe_keras_model(_ensure_model.model)
    else:
        artifacts["model"]["loaded"] = False
        artifacts["model"]["load_error"] = model_msg
        status["model_runtime"] = {"loaded": False, "error": model_msg}

    thr_value = getattr(_ensure_model, "thr", None)
    thr_source = "model_cache" if thr_value is not None else None
    if thr_value is None:
        thr_value = read_threshold(THR_PATH)
        if thr_value is not None:
            thr_source = "file"

    if thr_value is not None:
        status["threshold"] = float(thr_value)
        artifacts["threshold"]["value"] = float(thr_value)
        if thr_source:
            artifacts["threshold"]["source"] = thr_source

    return jsonify(status)

# ========= /export_onnx =========
@app.route("/export_onnx", methods=["POST"])
def export_onnx():
    """
    Exporta el modelo actual a ONNX: model/current_model.h5 -> model/current_model.onnx
    """
    try:
        ok, msg = _ensure_model()
        if not ok:
            return jsonify({"error": f"model not loaded: {msg}"}), 503

        try:
            import tf2onnx  # type: ignore
        except Exception as e:
            return jsonify({"error": "tf2onnx not installed", "detail": str(e)}), 500

        onnx_path = MODEL_DIR / "current_model.onnx"
        spec = (tf.TensorSpec((None, 600, 600, 3), tf.float32, name="input"),)
        _ = tf2onnx.convert.from_keras(
            _ensure_model.model, input_signature=spec, output_path=str(onnx_path), opset=13
        )
        return jsonify({"ok": True, "onnx_path": str(onnx_path)})
    except Exception as e:
        return jsonify({"error": "export_failed", "detail": str(e)}), 500


if __name__ == "__main__":
    app.run(host="0.0.0.0", port=5000, debug=False)
