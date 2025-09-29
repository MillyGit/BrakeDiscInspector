# API_REFERENCE — BrakeDiscInspector

Este documento describe los endpoints expuestos por el backend **FastAPI** de BrakeDiscInspector, sus parámetros, respuestas y ejemplos prácticos.

---

## 1) Endpoints principales

### 1.1 `GET /health`

- **Descripción**: Comprueba que el servicio está disponible y muestra información de dispositivo/modelo.
- **URL base**: `http://<host>:8000/health`

**Respuesta 200 OK**
```json
{
  "status": "ok",
  "device": "cuda",
  "model": "vit_small_patch14_dinov2.lvd142m",
  "version": "0.1.0"
}
```

### 1.2 `POST /fit_ok`

- **Descripción**: Recibe lotes de ROIs OK y construye/actualiza la memoria PatchCore (coreset + índice).
- **Método**: `multipart/form-data`
- **Campos obligatorios**:
  - `role_id` *(str)* — Identificador del rol/preset (ej. `Master1`).
  - `roi_id` *(str)* — Identificador de la ROI (ej. `Pattern`).
  - `mm_per_px` *(float)* — Resolución de la ROI para reporting (mm por pixel).
  - `images` *(files[])* — Uno o más PNG/JPG del ROI canónico (crop + rotación ya aplicados por la GUI).

**Respuesta 200 OK**
```json
{
  "n_embeddings": 34992,
  "coreset_size": 700,
  "token_shape": [32, 32],
  "coreset_rate_requested": 0.02,
  "coreset_rate_applied": 0.018
}
```

**Ejemplo `curl`**
```bash
curl -X POST http://127.0.0.1:8000/fit_ok \
  -F role_id=Master1 \
  -F roi_id=Pattern \
  -F mm_per_px=0.20 \
  -F images=@datasets/Master1/Pattern/ok/sample_001.png \
  -F images=@datasets/Master1/Pattern/ok/sample_002.png
```

### 1.3 `POST /calibrate_ng`

- **Descripción**: Calcula y persiste el umbral óptimo por `(role_id, roi_id)` a partir de scores OK/NG.
- **Método**: `application/json`
- **Body**:
  ```json
  {
    "role_id": "Master1",
    "roi_id": "Pattern",
    "mm_per_px": 0.20,
    "ok_scores": [12.1, 10.8, 11.5],
    "ng_scores": [28.4],
    "area_mm2_thr": 1.0,
    "score_percentile": 99
  }
  ```
- `ng_scores` es opcional; si se omite, el umbral será `p99(OK)`.

**Respuesta 200 OK**
```json
{
  "threshold": 20.0,
  "p99_ok": 12.0,
  "p5_ng": 28.0,
  "mm_per_px": 0.2,
  "area_mm2_thr": 1.0,
  "score_percentile": 99
}
```

### 1.4 `POST /infer`

- **Descripción**: Ejecuta inferencia sobre un ROI canónico usando la memoria entrenada.
- **Método**: `multipart/form-data`
- **Campos**:
  - `role_id`, `roi_id`, `mm_per_px` — mismos valores utilizados durante el entrenamiento/calibración.
  - `image` *(file)* — PNG/JPG del ROI canónico.
  - `shape` *(string, opcional)* — JSON que describe la máscara en coordenadas del ROI (`rect`, `circle`, `annulus`).

**Respuesta 200 OK (resumen)**
```json
{
  "role_id": "Master1",
  "roi_id": "Pattern",
  "score": 18.7,
  "threshold": 20.0,
  "heatmap_png_base64": "iVBORw0K...",
  "regions": [
    {"bbox": [x, y, w, h], "area_px": 250.0, "area_mm2": 10.0, "contour": [[x1, y1], ...]}
  ],
  "token_shape": [32, 32],
  "params": {
    "extractor": "vit_small_patch14_dinov2.lvd142m",
    "input_size": 448,
    "patch_size": 14,
    "coreset_rate": 0.02,
    "score_percentile": 99,
    "mm_per_px": 0.2
  }
}
```

**Ejemplo `curl`**
```bash
curl -X POST http://127.0.0.1:8000/infer \
  -F role_id=Master1 \
  -F roi_id=Pattern \
  -F mm_per_px=0.20 \
  -F image=@datasets/Master1/Pattern/ok/sample_eval.png \
  -F shape='{"kind":"circle","cx":192,"cy":192,"r":180}'
```

**Códigos de error**
- `400 Bad Request`: memoria inexistente para `(role_id, roi_id)` o datos inválidos.
- `422 Unprocessable Entity`: payload JSON malformado.
- `500 Internal Server Error`: excepciones no controladas (se devuelve `{ "error", "trace" }`).

---

## 2) Máscaras (`shape`) soportadas

Todas las coordenadas se expresan en el espacio del ROI canónico (post crop + rotación):

- **Rectángulo completo**:
  ```json
  {"kind":"rect","x":0,"y":0,"w":W,"h":H}
  ```
- **Círculo**:
  ```json
  {"kind":"circle","cx":CX,"cy":CY,"r":R}
  ```
- **Annulus**:
  ```json
  {"kind":"annulus","cx":CX,"cy":CY,"r":R_OUTER,"r_inner":R_INNER}
  ```

Si no se envía `shape`, el backend asume la imagen completa.

---

## 3) Persistencia de artefactos

Tras un `POST /fit_ok` exitoso, se genera la carpeta `backend/models/<role>/<roi>/` con:

- `memory.npz` — embeddings del coreset (`emb`), `token_h`, `token_w`, `metadata` (coreset rate aplicado).
- `index.faiss` — índice serializado (si FAISS está disponible).
- `calib.json` — creado por `/calibrate_ng` con `threshold`, percentiles y parámetros de área.

Los endpoints `/infer` y `/calibrate_ng` leen estos artefactos para mantener consistencia entre ejecuciones.

---

## 4) Ejemplos adicionales

### 4.1 Obtener scores para calibración manual

Para recolectar scores de muestras NG antes de llamar a `/calibrate_ng`:
```bash
curl -X POST http://127.0.0.1:8000/infer \
  -F role_id=Master1 \
  -F roi_id=Pattern \
  -F mm_per_px=0.20 \
  -F image=@datasets/Master1/Pattern/ng/sample_ng.png \
  -F shape='{"kind":"rect","x":0,"y":0,"w":384,"h":384}' \
  | jq '.score'
```

### 4.2 Reentrenar con muestras adicionales

Repite `/fit_ok` tantas veces como sea necesario; el backend sobrescribe `memory.npz` e índice con los embeddings recalculados.

### 4.3 Limpieza de artefactos

Para reiniciar un rol/ROI:
```bash
rm -rf backend/models/Master1/Pattern
```
La próxima llamada a `/infer` devolverá `400` indicando que falta memoria.

---

## 5) Convenciones generales

- Imágenes esperadas: PNG/JPG en BGR (`cv2.imdecode`).
- Tamaño de entrada: se reescala internamente al múltiplo de 14 más cercano al `input_size` configurado (448 por defecto).
- Percentil de score: configurable vía `score_percentile` (99 por defecto) y persistido en `calib.json`.
- Respuestas de error: `{ "error": "mensaje", "trace": "stacktrace" }` para facilitar debugging desde la GUI.

Para más detalles sobre formatos JSON y almacenamiento en disco consulta [DATA_FORMATS.md](DATA_FORMATS.md) y [backend/README_backend.md](backend/README_backend.md).
