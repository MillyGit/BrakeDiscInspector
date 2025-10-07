
# 📌 Actualización — 2025-10-07

**Cambios clave (GUI):**
- Corrección de salto del frame al clicar adorner (círculo/annulus): cálculo y propagación del centro reales en `SyncModelFromShape` y sincronización `X,Y = CX,CY` en `CreateLayoutShape`.
- Bbox SIEMPRE cuadrado para circle/annulus; overlay heatmap alineado.
- Decisiones del proyecto y parámetros vigentes documentados.

**Cambios clave (Backend):**
- PatchCore + DINOv2 ViT-S/14; endpoints `/health`, `/fit_ok`, `/calibrate_ng`, `/infer`; persistencia por `(role_id, roi_id)`.

# Instructions for Codex — WPF GUI: Dataset → Train (fit_ok) → Calibrate → Infer (backend PatchCore+DINOv2)

**Goal**: Implement in the existing WPF GUI a complete workflow to (1) collect ROI samples, (2) train the model memory on the backend, (3) optionally calibrate thresholds, and (4) run inference — all **without changing adorner/ROI drawing logic** or the backend service contract.

---

## Quick index

- [Scope & Non-Regression Rules](#scope--non-regression-rules)
- [Backend Contract](#backend-contract-summary)
- [New GUI Features](#new-gui-features-tabs-or-wizard)
- [Shape JSON mapping](#shape-json-mapping-from-existing-roi-types)
- [File/Folder Structure](#filefolder-structure-gui-side)
- [New/Updated Code](#newupdated-code-wpf)
- [Error Handling & UX](#error-handling--ux)
- [Acceptance Criteria](#acceptance-criteria)
- [Testing Plan](#testing-plan)
- [Coding Standards](#coding-standards)
- [Deliverables](#deliverables)
- [Do/Don’t Summary](#dodont-summary)

---

## Scope & Non‑Regression Rules

1. **Scope**: Modify **GUI (WPF)** only. The backend (FastAPI) already exposes `/fit_ok`, `/calibrate_ng`, `/infer`, `/health`.
2. **Must NOT change** any of these GUI subsystems, to avoid regressions in ROI alignment:
   - Adorners (`RoiAdorner`, `ResizeAdorner`, `RoiRotateAdorner`) and overlay (`RoiOverlay`).
   - ROI coordinate spaces and the image/canvas letterboxing logic.
   - The ROI canonicalization logic (crop+rotation) used by the existing “Save Master”/“Save Pattern”. Reuse it.
3. **Threading/UI**: All HTTP calls must be **async**. Do not block the UI thread. Disable buttons while requests are in-flight.
4. **Logging**: Use the GUI’s existing logging method (e.g., `AppendLog(...)`) to trace user actions and backend replies. Include timings and file paths.
5. **Configuration**: The backend base URL must be configurable in the GUI (default `http://127.0.0.1:8000`).

---

## Backend Contract (summary)

- `GET /health` → status, device, model, version.
- `POST /fit_ok` (multipart): `role_id`, `roi_id`, `mm_per_px`, multiple `images[]` (PNG/JPG). Returns `n_embeddings`, `coreset_size`, `token_shape`.
- `POST /calibrate_ng` (JSON): `role_id`, `roi_id`, `mm_per_px`, arrays `ok_scores`, optional `ng_scores`, `area_mm2_thr`, `score_percentile`. Returns `threshold` and stats.
- `POST /infer` (multipart): `role_id`, `roi_id`, `mm_per_px`, `image`, optional `shape` (JSON string). Returns `score`, `threshold` (0 if none), `heatmap_png_base64`, `regions` (bbox + area px/mm²).

> **Important**: Backend expects **canonical ROI image** (already cropped+rotated by GUI). The optional `shape` is used to mask inference (rect/circle/annulus).

---

## New GUI Features (Tabs or Wizard)

### A) **Dataset** (per role/ROI)
Controls:
- Selectors: `RoleId` (e.g., `Master1`, `Inspection`, ...), `RoiId` (e.g., `Pattern`, `Search`, ...).
- Numeric: `MmPerPx` (pre-filled from your camera/layout).
- Lists with thumbnails: `OK Samples`, `NG Samples` (optional).
- Buttons:
  - **“Add OK from Current ROI”**
  - **“Add NG from Current ROI”** (optional)
  - **“Remove Selected”**
  - **“Open Dataset Folder”**

Behaviour:
- On “Add OK/NG from Current ROI”:
  1. Canonicalize ROI (same routine used by your current Save Master/Pattern): **crop + rotation**.
     - **Reuse** your internal pipeline (e.g., `TryBuildRoiCropInfo(...)` → `TryGetRotatedCrop(...)`). **Do not rewrite adorner code**.
  2. Save PNG to: `datasets/<role>/<roi>/<ok|ng>/SAMPLE_yyyyMMdd_HHmmssfff.png`.
  3. Save metadata JSON next to image:
     ```json
     {
       "role_id": "Master1",
       "roi_id": "Pattern",
       "mm_per_px": 0.20,
       "shape": { "kind": "circle", "cx": 192, "cy": 192, "r": 180 },
       "source_path": "C:\images\part.png",
       "angle": 32.0,
       "timestamp": "2025-09-28T12:34:56.789Z"
     }
     ```
  4. Refresh the dataset list in UI.

### B) **Train / Calibrate**
- **“Train memory (fit_ok)”**: bundles all OK PNGs of current `(RoleId, RoiId)` and calls backend `/fit_ok`.
  - Display result: `n_embeddings`, `coreset_size`, `token_shape`.
- **“Calibrate threshold (calibrate_ng)”**:
  - Option A: If you have zero or few NGs, let the user skip or set percentile (p95/p99).
  - Option B: If you have NGs or want score-driven calibration:
    - Compute **scores** by calling `/infer` for all OK and NG samples (with no threshold). Collect `score` values.
    - Send arrays to `/calibrate_ng` and show returned `threshold`.

### C) **Inference**
- **“Evaluate Current ROI”**: canonicalize and POST to `/infer` with `shape` JSON built from the current ROI.
- Overlay returned `heatmap` (decode base64 PNG) on the ROI in the preview (with opacity slider).
- Show `score`, `threshold`, and list `regions` (bbox, area px, area mm²).
- Add a **local threshold slider** for interactive exploration (does not change backend threshold unless user confirms a “Save Threshold” action).

---

## Shape JSON mapping (from existing ROI types)

Produce one of these JSONs (as string) for the `shape` field in `/infer`:

- **Rectangle**:
  ```json
  {"kind":"rect","x":0,"y":0,"w":W,"h":H}
  ```
  (If the exported ROI is exactly the rectangle, you can use full canvas: 0,0,W,H)

- **Circle**:
  ```json
  {"kind":"circle","cx":CX,"cy":CY,"r":R}
  ```

- **Annulus**:
  ```json
  {"kind":"annulus","cx":CX,"cy":CY,"r":R_OUTER,"r_inner":R_INNER}
  ```

All coordinates are **in the canonical ROI image space** (pixels), **after** crop/rotation.

---

## File/Folder Structure (GUI side)

- Dataset root: `datasets/<role>/<roi>/`
  - `ok/` → PNG + JSON metadata
  - `ng/` → PNG + JSON metadata
  - `manifest.json` (optional): counts, last trained version, last calibration, etc.

---

## New/Updated Code (WPF)

### 1) Backend client (C#)
Create `BackendClient` with:
- `Task<(int nEmb, int coreset, int[] tokenShape)> FitOkAsync(string roleId, string roiId, double mmPerPx, IEnumerable<string> okImagePaths)`
- `Task<CalibResult> CalibrateAsync(string roleId, string roiId, double mmPerPx, IEnumerable<double> okScores, IEnumerable<double>? ngScores = null, double areaMm2Thr = 1.0, int scorePercentile = 99)`
- `Task<InferResult> InferAsync(string roleId, string roiId, double mmPerPx, string imagePath, string? shapeJson = null)`

Use `HttpClient`, `MultipartFormDataContent`, async/await, and JSON (System.Text.Json). Timeout 120s.

### 2) ROI export
Add helper `ExportCurrentRoiCanonicalAsync(out string pngPath, out string shapeJson)`:
- Reuse the **same internal code path** used by the existing Save Master (e.g., `TryBuildRoiCropInfo(...)`, `TryGetRotatedCrop(...)`).
- Output a PNG (e.g., `Temp/roi_current.png`) and the corresponding `shapeJson` in canonical space.

### 3) Commands / ViewModel
- `AddOkFromCurrentRoiCommand`
- `AddNgFromCurrentRoiCommand` (optional)
- `TrainFitCommand`
- `CalibrateCommand`
- `InferFromCurrentRoiCommand`

Each command must guard against invalid state (no role/roi, no ROI drawn, missing backend URL, etc.) and show progress/logs.

### 4) Heatmap Overlay
- Decode `heatmap_png_base64` to byte[] and display as Image overlay.
- Provide opacity slider [0..1].
- Ensure no DPI/scaling mismatch: overlay exactly on the ROI preview (use the canonical ROI image size).

---

## Error Handling & UX

- Show backend error messages (and `trace` if present) in a collapsible log panel.
- Gracefully handle network errors, timeouts, or invalid responses.
- Disable buttons while a request is ongoing; re-enable on completion/failure.
- For long operations (many samples), show a progress bar (`i/n` files uploaded).

---

## Acceptance Criteria

- Able to add ≥50 OK samples and see them listed with thumbnails.
- `/fit_ok` succeeds and returns `n_embeddings > 0`, `coreset_size > 0`.
- If NG provided, `/calibrate_ng` returns a valid `threshold`.
- `/infer` returns non-empty `heatmap_png_base64` and a numeric `score`.
- Heatmap overlay matches the ROI visualization (no drift/scale errors).
- No regressions in ROI placement/adorner behaviour (window resize, image reload).

---

## Testing Plan

1. **Health**: GUI calls `/health` on startup; show device/model in status bar.
2. **Dataset**: Add 5 OK samples from different parts; verify file system & metadata are created.
3. **Train**: Run `/fit_ok`; validate counts. Retry train after adding 20 more OKs.
4. **Calibrate**: If you have NGs, compute scores via `/infer` (without threshold) and run `/calibrate_ng`; store/display returned threshold.
5. **Infer**: Evaluate ROI; verify heatmap overlays and `regions` appear; adjust local slider.
6. **Resize/Reload**: Check that overlays remain aligned after window maximize and image reload (non-regression).

---

## Coding Standards

- C# 10+, async/await everywhere for I/O.
- MVVM: Commands + ViewModels; no heavy logic in code-behind.
- Use `ObservableCollection<>` for sample lists; thumbnails generated in background.
- Keep all paths `Path.Combine(...)`; avoid hard-coded separators.
- Localize UI strings if your project already uses resources.

---

## Deliverables

- Updated XAML (new tabs/panels, bindings).
- `BackendClient.cs` (or similar) with 3 public methods (FitOkAsync, CalibrateAsync, InferAsync) y DTOs (`InferResult`, `CalibResult`, `Region`).
- ROI export helper (reusing existing crop+rotate pipeline).
- ViewModels/Commands for Dataset, Train/Calibrate, Infer.
- Basic unit/integration tests if your solution includes a testing project (optional).

---

## Do/Don’t Summary

- ✅ **Do**: Reuse existing ROI export pipeline (crop+rotate); keep adorner & overlay logic intact.
- ✅ **Do**: Make all backend calls async; add progress & logs.
- ✅ **Do**: Persist datasets under `datasets/<role>/<roi>/` with PNG + metadata JSON.
- ❌ **Don’t**: Change adorner geometry, overlay scaling, or canvas/image transforms.
- ❌ **Don’t**: Move ROI in canvas space or introduce new coordinate transforms.
- ❌ **Don’t**: Touch backend endpoints or their names.

If any ambiguity arises (method names for the existing canonical ROI export, or how to access `mm_per_px`), **ask before changing behaviour**.
