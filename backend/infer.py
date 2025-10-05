@app.post("/infer")
def infer(
    role_id: str = Form(...),
    roi_id: str = Form(...),
    mm_per_px: float = Form(...),
    image: UploadFile = File(...),
    shape: Optional[str] = Form(None),
):
    try:
        import json, base64, numpy as np
        try:
            import cv2  # opcional para PNG rápido
            _has_cv2 = True
        except Exception:
            _has_cv2 = False
            from PIL import Image
            import io

        # 1) Imagen y features (sólo para verificar grid)
        img = _read_image_file(image)
        emb, token_hw = _extractor.extract(img)

        # 2) Cargar memoria/coreset
        loaded = store.load_memory(role_id, roi_id)
        if loaded is None:
            return JSONResponse(status_code=400, content={"error": "Memoria no encontrada. Ejecuta /fit_ok primero."})
        emb_mem, token_hw_mem, metadata = loaded

        # 3) Validación de grid aquí (clara al usuario)
        if tuple(map(int, token_hw)) != tuple(map(int, token_hw_mem)):
            return JSONResponse(
                status_code=400,
                content={"error": f"Token grid mismatch: got {tuple(map(int,token_hw))}, expected {tuple(map(int,token_hw_mem))}"},
            )

        # 4) Reconstruir memoria (+FAISS si existe)
        mem = PatchCoreMemory(embeddings=emb_mem, index=None, coreset_rate=metadata.get("coreset_rate"))
        try:
            import faiss  # type: ignore
            blob = store.load_index_blob(role_id, roi_id)
            if blob is not None:
                idx = faiss.deserialize_index(np.frombuffer(blob, dtype=np.uint8))
                mem.index = idx
                mem.nn = None
        except Exception:
            pass

        # 5) Calibración (puede faltar)
        calib = store.load_calib(role_id, roi_id, default=None)
        thr = calib.get("threshold") if calib else None
        area_mm2_thr = calib.get("area_mm2_thr", 1.0) if calib else 1.0
        p_score = calib.get("score_percentile", 99) if calib else 99

        # 6) Shape (máscara) opcional
        shape_obj = json.loads(shape) if shape else None

        # 7) Crear engine con lo que tu __init__ soporte
        try:
            engine = InferenceEngine(_extractor, mem, token_hw_mem, mm_per_px=float(mm_per_px))
        except TypeError:
            # Si tu __init__ no acepta mm_per_px
            engine = InferenceEngine(_extractor, mem, token_hw_mem)

        # 8) Ejecutar run() (probar con token_shape_expected y si no reintentar sin él)
        try:
            res = engine.run(
                img,
                token_shape_expected=tuple(map(int, token_hw_mem)),
                shape=shape_obj,
                threshold=thr,
                area_mm2_thr=float(area_mm2_thr),
                score_percentile=int(p_score),
            )
        except TypeError:
            res = engine.run(
                img,
                shape=shape_obj,
                threshold=thr,
                area_mm2_thr=float(area_mm2_thr),
                score_percentile=int(p_score),
            )

        # 9) Normalizar salida (dict nuevo o tupla antigua)
        score: float
        regions = []
        heatmap_png_b64 = None
        token_shape_out = [int(token_hw_mem[0]), int(token_hw_mem[1])]

        if isinstance(res, dict):
            score = float(res.get("score", 0.0))
            regions = res.get("regions") or []
            token_shape_out = list(res.get("token_shape") or token_shape_out)
            # heatmap puede venir como uint8 ("heatmap_u8") o como float32 ("heatmap")
            hm_u8 = res.get("heatmap_u8")
            if hm_u8 is None:
                hm = res.get("heatmap")
                if hm is not None:
                    hm_u8 = np.clip(np.asarray(hm, dtype=np.float32) * 255.0, 0, 255).astype(np.uint8)
            if hm_u8 is not None:
                if _has_cv2:
                    ok, png = cv2.imencode(".png", np.asarray(hm_u8, dtype=np.uint8))
                    if ok:
                        heatmap_png_b64 = base64.b64encode(png.tobytes()).decode("ascii")
                else:
                    pil = Image.fromarray(np.asarray(hm_u8, dtype=np.uint8), mode="L")
                    buf = io.BytesIO()
                    pil.save(buf, format="PNG")
                    heatmap_png_b64 = base64.b64encode(buf.getvalue()).decode("ascii")
        else:
            # Compat tupla antigua: (score, heatmap_float, regions)
            score, heatmap_f32, regions = res
            hm_u8 = np.clip(np.asarray(heatmap_f32, dtype=np.float32) * 255.0, 0, 255).astype(np.uint8)
            if _has_cv2:
                ok, png = cv2.imencode(".png", hm_u8)
                if ok:
                    heatmap_png_b64 = base64.b64encode(png.tobytes()).decode("ascii")
            else:
                pil = Image.fromarray(hm_u8, mode="L")
                buf = io.BytesIO()
                pil.save(buf, format="PNG")
                heatmap_png_b64 = base64.b64encode(buf.getvalue()).decode("ascii")

        # 10) Respuesta (threshold puede ser None → se serializa como null)
        return {
            "score": float(score),
            "threshold": (float(thr) if thr is not None else None),
            "token_shape": [int(token_shape_out[0]), int(token_shape_out[1])],
            "heatmap_png_base64": heatmap_png_b64,
            "regions": regions or [],
        }

    except Exception as e:
        import traceback
        return JSONResponse(status_code=500, content={"error": str(e), "trace": traceback.format_exc()})
