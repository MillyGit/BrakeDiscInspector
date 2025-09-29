# agents_for_backend.md — Backend Playbook (FastAPI • PatchCore • DINOv2)

This playbook is for assistants/agents implementing and maintaining the **backend anomaly detection service**.
The service is a **FastAPI** microservice that provides PatchCore-style anomaly detection with a **frozen DINOv2 ViT‑S/14** feature extractor.

> **Key contract**: The **GUI supplies a canonical ROI image** (already **cropped + rotated**).
> Backend must **not** change geometry (no extra crop/rotate). Optional `shape` only **masks** the heatmap (rect/circle/annulus).

---

## Quick index

- [Repository layout](#0-repository-layout-backend)
- [API (stable contract)](#1-api-stable-contract)
- [Persistence](#2-persistence-formats)
- [Feature extractor](#3-feature-extractor-dinov2)
- [PatchCore memory](#4-patchcore-memory)
- [Inference pipeline](#5-inference-pipeline)
- [Configuration](#6-configuration)
- [Validation & Errors](#7-validation--errors)

---

## 0) Repository layout (backend)

```
backend/
  app.py               # FastAPI: /fit_ok, /calibrate_ng, /infer, /health
  features.py          # DINOv2 ViT-S/14 frozen extractor via timm
  patchcore.py         # L2 normalize, k-center greedy coreset, kNN (FAISS or sklearn)
  infer.py             # Inference engine (distance map → score/regions)
  calib.py             # Threshold selection (p99(OK), p5(NG) if available)
  roi_mask.py          # ROI mask builders (rect/circle/annulus) in canonical space
  storage.py           # Persistence under models/<role>/<roi>/
  utils.py             # Helpers (I/O, base64, percentiles, mm/px conversions)
  requirements.txt
  README_backend.md
models/
  <role>/<roi>/memory.npz
  <role>/<roi>/index.faiss      # optional
  <role>/<roi>/calib.json
```

**Non‑regression**: Keep this layout and file formats stable for compatibility with the GUI and older models.

---

## 1) API (stable contract)

### `GET /health`
Returns service status, device and model info.

### `POST /fit_ok`  *(multipart)*
- Fields: `role_id`, `roi_id`, `mm_per_px`, `images[]` (1..N **canonical ROI** PNG/JPG)
- Action: Extract patch embeddings, L2-normalize, coreset (k-center greedy), persist memory + optional FAISS index.
- Response JSON:
  ```json
  { "n_embeddings": 34992, "coreset_size": 700, "token_shape": [Ht, Wt] }
  ```

### `POST /calibrate_ng`  *(application/json)*
- Body: `{ role_id, roi_id, mm_per_px, ok_scores[], ng_scores?[], area_mm2_thr?, score_percentile? }`
- Action: Choose threshold (default p99(OK); if NG exists, midpoint between p99(OK) and p5(NG)). Persist calib under ROI.
- Response JSON includes: `{ threshold, p99_ok?, p5_ng?, mm_per_px, area_mm2_thr, score_percentile }`

### `POST /infer`  *(multipart)*
- Fields: `role_id`, `roi_id`, `mm_per_px`, `image` (canonical ROI), `shape?` (JSON string)
- Action: Extract features → distance map (kNN) → upsample to ROI size → normalize → apply `shape` mask → score = percentile (default p99) → optional thresholding + small-island removal → contours.
- Response JSON:
  ```json
  {
    "role_id": "Master1",
    "roi_id": "Pattern",
    "score": 18.7,
    "threshold": 20.0,
    "heatmap_png_base64": "iVBORw0K...",
    "regions": [ {"bbox":[x,y,w,h], "area_px":250.0, "area_mm2":10.0} ],
    "token_shape": [Ht, Wt]
  }
  ```

**Error shape** (all endpoints): `{"error": "<message>", "trace": "<stacktrace>"}` (HTTP 4xx/5xx).

---

## 2) Persistence (formats)

- `models/<role>/<roi>/memory.npz`
  - `emb`: `float32` array (coreset embeddings), **L2-normalized**
  - `token_h`, `token_w`: ints describing the feature grid (Ht, Wt)
- `index.faiss` *(optional)*: serialized FAISS index (FlatL2). If absent, sklearn NearestNeighbors is used.
- `calib.json`
  ```json
  {
    "threshold": 20.0,
    "p99_ok": 12.0,
    "p5_ng": 28.0,
    "mm_per_px": 0.20,
    "area_mm2_thr": 1.0,
    "score_percentile": 99
  }
  ```

**Do not** change these keys/types without versioning & migration.

---

## 3) Feature extractor (DINOv2)

- Model: `vit_small_patch14_dinov2.lvd142m` via **timm**.
- **Frozen** (no grad), `eval()` mode, moved to chosen device.
- Input preproc:
  - Resize to `input_size` (default **448**), **snapped to multiple of 14**.
  - Convert BGR→RGB; normalize with ImageNet mean/std.
- Feature tensor selection:
  - Accept dict or tensor from `forward_features`.
  - Prefer token grid (`[N_tokens, D]`), handle CLS if present (`n-1` square check).
- Return `(embeddings: [N,D], token_hw: (H,W))`.

**Do not** introduce training heads or finetune unless explicitly requested.

---

## 4) PatchCore memory

- Normalize embeddings (L2).
- Coreset via **k-center greedy**; configurable `coreset_rate` (default **0.02**).
- Index: **FAISS FlatL2** if available; else fallback to `sklearn.NearestNeighbors`.
- Query: **min distance** (k=1) for each patch.

**Performance tips**:
- Keep coreset small (1–5%). Save FAISS index when present.
- CPU mode works; GPU accelerates extractor and FAISS (not required).

---

## 5) Inference pipeline

1. Extract embeddings from ROI image → reshape to `(Ht, Wt)` grid of distances.
2. Upsample distance map to ROI (H×W).
3. Optional Gaussian blur (σ≈1.0; kernel derived).
4. Robust normalize to 0..255 (1–99 percentile) for visualization.
5. Build **mask** (`roi_mask.py`) from `shape` (rect/circle/annulus) in **canonical ROI coordinates**; apply it.
6. **Score** = percentile (p99 by default) over masked values.
7. If a threshold is provided (from calib), binarize, drop **small islands** by `area_mm2_thr` → px² using `mm_per_px` → contours → `regions`.

**Do not** re-crop or rotate the ROI. Only mask in canonical space.

---

## 6) Configuration

Use constants or env vars (suggested):
- `MODEL_NAME` (default `vit_small_patch14_dinov2.lvd142m`)
- `DEVICE` (`auto`|`cpu`|`cuda`)
- `INPUT_SIZE` (default 448; must be multiple of 14 after snapping)
- `CORESET_RATE` (default 0.02)
- `MODELS_DIR` (default `models/`)

Keep **defaults** aligned with the checked-in code to avoid drift with GUI docs.

---

## 7) Validation & Errors

- Validate file decodes (OpenCV `imdecode` not None).
- Verify that memory exists for `(role, roi)` before `/infer` (return 400 with message).
- For `/calibrate_ng`, require at least one OK score; NG optional.
- Always return structured errors with **HTTP code + message + trace**.
- Limit request size if deploying multi-tenant (FastAPI/ASGI settings or reverse proxy).

---

## 8) Logging & Telemetry

- Log timings for: decode, extract, coreset build, kNN search, PNG encode.
- Log `(role_id, roi_id)`, `n_images`, `token_shape`, device.
- Avoid logging raw images. If needed, log small thumbnails or dimensions only.
- Ensure logs don’t leak PII or paths if running shared.

---

## 9) Security & Safety

- Reject executable file types; only accept image content types for uploads.
- Consider request size/time limits and a reverse proxy (Nginx) for rate limiting.
- Sanitize `role_id`, `roi_id` to path‑safe tokens (avoid `..`, slashes). Storage layer should join paths safely.
- Do not execute arbitrary code, shell, or eval user inputs.

---

## 10) Testing

### Unit tests (suggestions)
- `features.py`: deterministic shapes `(Ht, Wt)` for a fixed `input_size`. Mock timm if necessary.
- `patchcore.py`: coreset selection shape; L2 normalization; FAISS fallback path.
- `roi_mask.py`: rect/circle/annulus masks sizes and intersections.
- `infer.py`: score monotonicity when injecting synthetic hotspots; island removal with `mm_per_px` conversion.
- `storage.py`: read/write roundtrip for memory.npz and calib.json.

### Integration tests
- Boot app with uvicorn in test mode.
- `/fit_ok` with a few synthetic ROI images (e.g., solid colors + blobs).
- `/infer` returns non-empty heatmap and reasonable score.
- `/calibrate_ng` returns numeric threshold; subsequent `/infer` applies it (regions populated).

---

## 11) Deployment

### Uvicorn (dev)
```
uvicorn backend.app:app --host 127.0.0.1 --port 8000
```

### Gunicorn (prod, workers>1)
```
gunicorn -k uvicorn.workers.UvicornWorker backend.app:app -w 2 -b 127.0.0.1:8000
```

### Minimal Dockerfile (example)
```dockerfile
FROM python:3.10-slim
WORKDIR /app
COPY backend/ /app/backend/
RUN pip install --no-cache-dir -r backend/requirements.txt
EXPOSE 8000
CMD ["uvicorn", "backend.app:app", "--host", "127.0.0.1", "--port", "8000"]
```

Mount or bake `models/` volume for persistence.

---

## 12) Backward compatibility

- Preserve endpoint names and request fields.
- Keep persistence keys (`emb`, `token_h`, `token_w`, `threshold`, etc.).
- If changes are inevitable, bump a `version` in `/health` and add migration code for old `memory.npz/calib.json` formats.

---

## 13) Performance tips

- Prefer **float32** for embeddings; avoid unnecessary copies.
- Batch feature extraction within a request if many images are sent to `/fit_ok`.
- Use FAISS if available; otherwise sklearn is acceptable for modest coreset sizes.
- Keep coreset rate low (1–2%) for large datasets; tune per ROI.

---

## 14) FAQ

- **Q: Can backend rotate/crop for me?**  
  **A:** No. GUI already canonicalizes ROI; backend must not change geometry. Only masking is allowed.

- **Q: Why percentiles for score?**  
  Robust to outliers and stabilizes across ROIs. p99/p95 work well; adjustable via calibration.

- **Q: What if FAISS isn’t available?**  
  Fallback to `NearestNeighbors` (CPU). It’s slower but functional.

- **Q: How to map mm² thresholds?**  
  Convert `area_mm2_thr` → px² with `mm_per_px` from the GUI.

---

## 15) Ready references

- See `README_backend.md` for run instructions and curl examples.
- Code entry points: `app.py` (routes), `InferenceEngine.run(...)`, `PatchCoreMemory.build(...)`, `DinoV2Features.extract(...)`.
- Masks: `roi_mask.py` for `rect`, `circle`, `annulus`. All in canonical ROI coordinates.

---

**Golden rule**: **Do not** modify geometry. The backend takes a **canonical ROI** image and returns anomaly maps & scores consistent with that exact pixel grid.
