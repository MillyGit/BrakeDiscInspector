from __future__ import annotations
import json
import traceback
from typing import Optional, Dict, Any, List
from pathlib import Path

import numpy as np
import cv2

from fastapi import FastAPI, UploadFile, File, Form
from fastapi.responses import JSONResponse

from .features import DinoV2Features
from .patchcore import PatchCoreMemory
from .storage import ModelStore
from .infer import InferenceEngine
from .calib import choose_threshold
from .utils import ensure_dir, base64_from_bytes

app = FastAPI(title="Anomaly Backend (PatchCore + DINOv2)")

# Carpeta para artefactos persistentes por (role_id, roi_id)
MODELS_DIR = Path("models")
ensure_dir(MODELS_DIR)
store = ModelStore(MODELS_DIR)

# Carga única del extractor (congelado)
_extractor = DinoV2Features(
    model_name="vit_small_patch14_dinov2.lvd142m",
    device="auto",
    input_size=448,   # múltiplo de 14; si envías 384, el extractor reescala internamente
    patch_size=14
)

def _read_image_file(file: UploadFile) -> np.ndarray:
    data = file.file.read()
    img_array = np.frombuffer(data, dtype=np.uint8)
    img = cv2.imdecode(img_array, cv2.IMREAD_COLOR)
    if img is None:
        raise ValueError("No se pudo decodificar la imagen")
    return img

@app.get("/health")
def health():
    import torch
    return {
        "status": "ok",
        "device": "cuda" if torch.cuda.is_available() else "cpu",
        "model": "vit_small_patch14_dinov2.lvd142m",
        "version": "0.1.0",
    }

@app.post("/fit_ok")
def fit_ok(
    role_id: str = Form(...),
    roi_id: str = Form(...),
    mm_per_px: float = Form(...),
    images: List[UploadFile] = File(...),
):
    """
    Acumula OKs para construir la memoria PatchCore (coreset + kNN).
    Devuelve: nº total de embeddings, tamaño del coreset y forma del grid de tokens.
    """
    try:
        all_emb = []
        token_hw = None
        for f in images:
            img = _read_image_file(f)
            emb, hw = _extractor.extract(img)
            all_emb.append(emb)
            token_hw = hw

        if token_hw is None:
            return JSONResponse(status_code=400, content={"error": "No se recibieron imágenes válidas"})

        E = np.concatenate(all_emb, axis=0)  # (N, D)
        mem = PatchCoreMemory.build(E, coreset_rate=0.02, seed=0)

        # Persistir memoria + token grid
        store.save_memory(role_id, roi_id, mem.emb, token_hw)

        # Persistir índice FAISS si está disponible
        try:
            import faiss  # type: ignore
            if mem.index is not None:
                buf = faiss.serialize_index(mem.index)
                store.save_index_blob(role_id, roi_id, bytes(buf))
        except Exception:
            pass

        return {
            "n_embeddings": int(E.shape[0]),
            "coreset_size": int(mem.emb.shape[0]),
            "token_shape": [int(token_hw[0]), int(token_hw[1])],
        }
    except Exception as e:
        return JSONResponse(status_code=500, content={"error": str(e), "trace": traceback.format_exc()})

@app.post("/calibrate_ng")
async def calibrate_ng(payload: Dict[str, Any]):
    """
    Fija umbral por ROI/rol con 0–3 NG.
    Si hay NG: umbral entre p99(OK) y p5(NG). Si no: p99(OK).
    """
    try:
        role_id = payload["role_id"]
        roi_id = payload["roi_id"]
        mm_per_px = float(payload.get("mm_per_px", 0.2))
        ok_scores = np.asarray(payload.get("ok_scores", []), dtype=float)
        ng_scores = np.asarray(payload.get("ng_scores", []), dtype=float) if "ng_scores" in payload else None
        area_mm2_thr = float(payload.get("area_mm2_thr", 1.0))
        p_score = int(payload.get("score_percentile", 99))

        t = choose_threshold(ok_scores, ng_scores if (ng_scores is not None and ng_scores.size > 0) else None,
                             percentile=p_score)

        calib = {
            "threshold": float(t),
            "p99_ok": float(np.percentile(ok_scores, p_score)) if ok_scores.size > 0 else None,
            "p5_ng": float(np.percentile(ng_scores, 5)) if (ng_scores is not None and ng_scores.size > 0) else None,
            "mm_per_px": float(mm_per_px),
            "area_mm2_thr": float(area_mm2_thr),
            "score_percentile": int(p_score),
        }
        store.save_calib(role_id, roi_id, calib)
        return calib
    except Exception as e:
        return JSONResponse(status_code=500, content={"error": str(e), "trace": traceback.format_exc()})

@app.post("/infer")
def infer(
    role_id: str = Form(...),
    roi_id: str = Form(...),
    mm_per_px: float = Form(...),
    image: UploadFile = File(...),
    shape: Optional[str] = Form(None),
):
    """
    Inferencia sobre un ROI canónico:
      - Extrae embeddings (DINOv2)
      - kNN al coreset (PatchCore) → heatmap de distancias
      - Score global (percentil) + posproceso (blur, máscara, umbral, islas, contornos)
    Devuelve JSON con score, threshold, heatmap en base64 y regiones.
    """
    try:
        img = _read_image_file(image)

        loaded = store.load_memory(role_id, roi_id)
        if loaded is None:
            return JSONResponse(status_code=400, content={"error": "Memoria no encontrada. Ejecuta /fit_ok primero."})
        emb, token_hw = loaded

        # Reconstruir memoria + índice
        mem = PatchCoreMemory(embeddings=emb, index=None)
        try:
            import faiss  # type: ignore
            blob = store.load_index_blob(role_id, roi_id)
            if blob is not None:
                idx = faiss.deserialize_index(np.frombuffer(blob, dtype=np.uint8))
                mem.index = idx
                mem.nn = None  # usamos FAISS
        except Exception:
            pass

        calib = store.load_calib(role_id, roi_id, default=None)
        thr = calib.get("threshold") if calib else None
        area_mm2_thr = calib.get("area_mm2_thr", 1.0) if calib else 1.0
        p_score = calib.get("score_percentile", 99) if calib else 99

        shape_obj = json.loads(shape) if shape else None

        engine = InferenceEngine(_extractor, mem, token_hw, mm_per_px, k=1, score_percentile=p_score)
        out = engine.run(img, shape=shape_obj, blur_sigma=1.0, area_mm2_thr=area_mm2_thr, threshold=thr)

        # Empaquetar heatmap como PNG base64 para la GUI
        heat_u8 = out["heatmap_u8"]
        ok, buf = cv2.imencode(".png", heat_u8)
        if not ok:
            raise RuntimeError("No se pudo codificar heatmap")
        out["heatmap_png_base64"] = base64_from_bytes(buf.tobytes())
        del out["heatmap_u8"]

        out["role_id"] = role_id
        out["roi_id"] = roi_id
        return out
    except Exception as e:
        return JSONResponse(status_code=500, content={"error": str(e), "trace": traceback.format_exc()})
