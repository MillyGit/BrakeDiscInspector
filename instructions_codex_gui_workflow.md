# 📌 Actualización — 2025-10-07

**Cambios clave documentados en esta versión:**
- Guía actualizada para el flujo GUI Dataset → `/fit_ok` → `/calibrate_ng` → `/infer` usando `BackendClient` y `DatasetManager` actuales.
- Se refuerzan las restricciones sobre adorners, canonicalización (`RoiCropUtils`) y máscaras `shape_json`.
- Se añaden referencias a DTOs y manejo de errores presente en `Workflow/BackendClient.cs`.

# Instructions for Agents — WPF GUI workflow (Dataset → Train → Calibrate → Infer)

**Goal**: Mantener y extender la GUI WPF sin romper el pipeline de ROI canónico. Toda interacción con el backend PatchCore+DINOv2 debe respetar las rutas `/health`, `/fit_ok`, `/calibrate_ng`, `/infer` implementadas en `backend/app.py`.

---

## 1. Scope & Non-Regression Rules

1. **No modificar** adorners ni overlays base (`RoiAdorner`, `ResizeAdorner`, `RoiRotateAdorner`, `RoiOverlay`).【F:gui/BrakeDiscInspector_GUI_ROI/RoiAdorner.cs†L1-L200】
2. Reutilizar `RoiCropUtils` para exportar ROIs canónicos; no reescribir transformaciones (`TryBuildRoiCropInfo`, `TryGetRotatedCrop`).【F:gui/BrakeDiscInspector_GUI_ROI/RoiCropUtils.cs†L62-L134】
3. Todas las llamadas HTTP deben ser `async` (usar `BackendClient`), evitando bloquear el hilo UI.【F:gui/BrakeDiscInspector_GUI_ROI/Workflow/BackendClient.cs†L20-L218】
4. No cambiar nombres de campos enviados al backend (`role_id`, `roi_id`, `mm_per_px`, `images`, `shape`).
5. Mantener la estructura de datasets `datasets/<role>/<roi>/<ok|ng>/` (PNG + JSON con `shape_json`, `mm_per_px`, `angle`).【F:gui/BrakeDiscInspector_GUI_ROI/Workflow/DatasetManager.cs†L38-L74】

---

## 2. Backend Contract (summary)

- `GET /health` → `{ status, device, model, version }`.
- `POST /fit_ok` (multipart) → campos `role_id`, `roi_id`, `mm_per_px`, `images[]`; respuesta con `n_embeddings`, `coreset_size`, `token_shape`.
- `POST /calibrate_ng` (JSON) → `role_id`, `roi_id`, `mm_per_px`, `ok_scores[]`, opcional `ng_scores[]`, `area_mm2_thr`, `score_percentile`; respuesta con `threshold`, `p99_ok`, `p5_ng`.
- `POST /infer` (multipart) → `role_id`, `roi_id`, `mm_per_px`, `image`, opcional `shape`; respuesta con `score`, `threshold?`, `heatmap_png_base64`, `regions`, `token_shape`.

La GUI debe mapear estas respuestas a DTOs (`FitOkResult`, `CalibResult`, `InferResult`) usando `System.Text.Json` (`JsonSerializerDefaults.Web`).【F:gui/BrakeDiscInspector_GUI_ROI/Workflow/BackendClient.cs†L150-L218】

---

## 3. Features to maintain in the GUI

### Dataset tab
- Selectores `RoleId`, `RoiId`, `MmPerPx`.
- Botones **Add OK/NG from Current ROI** → exportar PNG+JSON usando `DatasetManager.SaveSampleAsync`.
- Listas `OK samples` / `NG samples` con thumbnails (`DatasetSample.Thumbnail`).
- Botón **Open folder** para abrir `datasets/<role>/<roi>/`.

### Train tab
- Botón **Train memory (fit_ok)** → empaquetar muestras OK (`images[]`) y llamar a `BackendClient.FitOkAsync`.
- Mostrar `n_embeddings`, `coreset_size`, `token_shape` devueltos.

### Calibrate tab
- Recopilar scores OK/NG (reutilizando `/infer` sin threshold) y llamar a `BackendClient.CalibrateAsync`.
- Mostrar `threshold`, `p99_ok`, `p5_ng`, `score_percentile`, `area_mm2_thr`.

### Infer tab
- Botón **Infer current ROI** → exportar ROI canónico temporal, construir `shape_json` y llamar a `BackendClient.InferAsync`.
- Mostrar `score`, `threshold`, nº `regions`, overlay del `heatmap_png_base64`.
- Slider de opacidad local (no modifica `threshold` backend).

---

## 4. Shape JSON mapping

- `Rectangle`: `{ "kind":"rect", "x":0, "y":0, "w":W, "h":H }`
- `Circle`: `{ "kind":"circle", "cx":CX, "cy":CY, "r":R }`
- `Annulus`: `{ "kind":"annulus", "cx":CX, "cy":CY, "r":R_OUTER, "r_inner":R_INNER }`

Las coordenadas siempre están en píxeles del ROI canónico (tras crop + rotación). Enviar como `StringContent` (`application/json` o texto) en el campo `shape` del multipart.

---

## 5. Error handling & UX

- Capturar `HttpRequestException` en cada llamada y mostrar mensaje amigable + detalles en panel de logs.
- Validar entradas antes de llamar al backend (role, roi, mm_per_px > 0, ROI dibujado, muestras disponibles).
- Deshabilitar botones mientras la tarea está en curso, reactivarlos en `finally`.
- Registrar en logs (GUI) el `CorrelationId` y la respuesta completa (`score`, `threshold`, `regions.Count`).

---

## 6. Testing Plan

1. **Startup**: llamar a `/health` y mostrar `device`/`model`.
2. **Dataset**: exportar ≥5 muestras OK/NG y verificar que se crean PNG+JSON en `datasets/`.
3. **Train**: ejecutar `/fit_ok`, comprobar `n_embeddings` y `coreset_size` positivos.
4. **Calibrate**: recolectar scores y enviar a `/calibrate_ng`, verificar `threshold` persistido.
5. **Infer**: llamar a `/infer`, superponer heatmap, listar `regions` (área px/mm²).
6. **Resize/Reload**: confirmar que la superposición se mantiene al redimensionar la ventana o recargar imagen.

---

## 7. Deliverables when extending the GUI

- Cambios en XAML (nuevos controles o bindings).
- Actualizaciones en ViewModels/Commands (`WorkflowViewModel`, comandos async).
- Ajustes en `BackendClient`, `DatasetManager`, `DatasetSample` si se introducen nuevos campos.
- Tests (unitarios o manuales) documentados en el PR.

---

## 8. Do / Don’t summary

| ✅ Hacer | ❌ No hacer |
|---------|-------------|
| Reutilizar pipeline de exportación (`RoiCropUtils`) | Alterar adorners, overlays o transformaciones base |
| Usar `async/await` en todos los requests | Bloquear el hilo UI con `.Result`/`.Wait()` |
| Guardar PNG+JSON en `datasets/<role>/<roi>/<label>/` | Cambiar la estructura de carpetas o nombres de archivo |
| Mapear DTOs a campos existentes (`score`, `threshold`, `regions`) | Renombrar campos esperados por el backend |
| Registrar operaciones y respuestas en logs GUI | Ignorar excepciones del backend o descartarlas silenciosamente |

---

Para dudas adicionales revisar [README.md](README.md), [DEV_GUIDE.md](DEV_GUIDE.md) y [API_REFERENCE.md](API_REFERENCE.md).
