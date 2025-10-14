# 📌 Actualización — 2025-10-07

**Cambios clave documentados en esta versión:**
- Contratos `/health`, `/fit_ok`, `/calibrate_ng`, `/infer` sincronizados con la implementación actual de `backend/app.py`.
- Ejemplos `curl` actualizados para dataset `datasets/<role>/<roi>/<ok|ng>/` y respuestas reales (`score`, `threshold`, `heatmap_png_base64`, `regions`).
- Detalles añadidos sobre máscaras (`shape`) y persistencia en `backend/models/<role>/<roi>/`.

# API_REFERENCE — BrakeDiscInspector

El backend FastAPI expone cuatro endpoints estables utilizados por la GUI WPF y por scripts externos. Este documento describe parámetros, respuestas, códigos de error y artefactos generados.

---

## Índice rápido

- [Endpoints principales](#1-endpoints-principales)
- [Máscaras soportadas](#2-máscaras-soportadas)
- [Persistencia de artefactos](#3-persistencia-de-artefactos)
- [Ejemplos adicionales](#4-ejemplos-adicionales)
- [Convenciones generales](#5-convenciones-generales)

---

## 1) Endpoints principales

### 1.1 `GET /health`

- **Descripción**: comprueba que el servicio está disponible y devuelve información del modelo base.
- **URL**: `http://<host>:<puerto>/health`
- **Respuesta 200 OK**
  ```json
  {
    "status": "ok",
    "device": "cuda",
    "model": "vit_small_patch14_dinov2.lvd142m",
    "version": "0.1.0"
  }
  ```

### 1.2 `POST /fit_ok`

- **Descripción**: construye la memoria PatchCore a partir de ROIs OK (PNG/JPG ya rotados y recortados por la GUI).
- **Tipo**: `multipart/form-data`
- **Campos obligatorios**:
  | Campo | Tipo | Descripción |
  |-------|------|-------------|
  | `role_id` | string | Rol actual (ej. `Master1`). |
  | `roi_id` | string | Identificador de la ROI (ej. `Pattern`). |
  | `mm_per_px` | float | Resolución del ROI (mm por pixel). |
  | `images` | fichero(s) | Uno o más PNG/JPG canónicos. |
- **Respuesta 200 OK**
  ```json
  {
    "n_embeddings": 34992,
    "coreset_size": 700,
    "token_shape": [32, 32],
    "coreset_rate_requested": 0.02,
    "coreset_rate_applied": 0.018
  }
  ```
- **Notas**:
  - Si las imágenes producen `token_shape` diferentes, la petición falla con 400 (`"Token grid mismatch"`).
  - El backend persiste `memory.npz`, `index.faiss` (si FAISS disponible) y metadata del coreset.【F:backend/app.py†L54-L118】【F:backend/storage.py†L12-L64】

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

- **Descripción**: calcula y guarda el umbral óptimo por `(role_id, roi_id)` usando scores OK (y opcionalmente NG).
- **Tipo**: `application/json`
- **Body**
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
- **Respuesta 200 OK**
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
- **Notas**:
  - `ng_scores` es opcional; si no se envía, el umbral se fija en `p99(ok_scores)`.
  - El resultado se guarda como `calib.json` bajo `backend/models/<role>/<roi>/`.【F:backend/app.py†L120-L166】【F:backend/storage.py†L66-L79】

### 1.4 `POST /infer`

- **Descripción**: ejecuta inferencia sobre un ROI canónico usando la memoria entrenada.
- **Tipo**: `multipart/form-data`
- **Campos**:
  | Campo | Tipo | Descripción |
  |-------|------|-------------|
  | `role_id` | string | Debe coincidir con el usado en `/fit_ok`. |
  | `roi_id` | string | Debe coincidir con el usado en `/fit_ok`. |
  | `mm_per_px` | float | Resolución para convertir áreas. |
  | `image` | fichero | ROI canónico (PNG/JPG). |
  | `shape` | string (opcional) | JSON con máscara (`rect`, `circle`, `annulus`). |
- **Respuesta 200 OK**
  ```json
  {
    "score": 18.7,
    "threshold": 20.0,
    "token_shape": [32, 32],
    "heatmap_png_base64": "iVBORw0K...",
    "regions": [
      {
        "bbox": [120, 96, 80, 64],
        "area_px": 250.0,
        "area_mm2": 10.0,
        "contour": [[120,96],[199,96],...]
      }
    ]
  }
  ```
- **Notas**:
  - `threshold` puede ser `null` si no se ejecutó `/calibrate_ng`.
  - La máscara `shape` se aplica antes de calcular percentiles y regiones.【F:backend/infer.py†L66-L132】【F:backend/app.py†L168-L214】
  - Los contornos se filtran por `area_mm2_thr` convertido a píxeles mediante `mm_per_px`.【F:backend/infer.py†L133-L181】

**Ejemplo `curl`**
```bash
curl -X POST http://127.0.0.1:8000/infer \
  -F role_id=Master1 \
  -F roi_id=Pattern \
  -F mm_per_px=0.20 \
  -F image=@datasets/Master1/Pattern/ok/sample_eval.png \
  -F shape='{"kind":"circle","cx":192,"cy":192,"r":180}'
```

**Errores comunes**
- `400` con `{ "error": "Memoria no encontrada..." }` cuando falta `memory.npz` para `(role_id, roi_id)`.
- `400` con `{ "error": "Token grid mismatch..." }` si el ROI de inferencia no coincide con el `token_shape` entrenado.
- `500` con `{ "error", "trace" }` cuando ocurre una excepción no controlada (revisar logs backend).

---

## 2) Máscaras soportadas

Las máscaras (`shape`) se expresan en píxeles del ROI canónico (post rotación/crop):

- **Rectángulo**
  ```json
  {"kind":"rect","x":0,"y":0,"w":W,"h":H}
  ```
- **Círculo**
  ```json
  {"kind":"circle","cx":CX,"cy":CY,"r":R}
  ```
- **Annulus**
  ```json
  {"kind":"annulus","cx":CX,"cy":CY,"r":R_OUTER,"r_inner":R_INNER}
  ```

Si no se envía `shape`, se considera todo el ROI. El backend construye las máscaras con `roi_mask.build_mask` antes de calcular heatmap y score.【F:backend/roi_mask.py†L1-L160】

---

## 3) Persistencia de artefactos

Tras un `/fit_ok` exitoso se crea `backend/models/<role>/<roi>/` con:

| Archivo | Descripción |
|---------|-------------|
| `memory.npz` | Embeddings del coreset (`emb`), `token_h`, `token_w`, metadata (`coreset_rate`, `applied_rate`). |
| `index.faiss` | Índice serializado (si FAISS está disponible). |
| `calib.json` | Creado por `/calibrate_ng` con `threshold`, percentiles, `mm_per_px`, `area_mm2_thr`. |

`/infer` reutiliza estos artefactos para reconstruir `PatchCoreMemory` y aplicar la calibración sin reentrenar.【F:backend/app.py†L134-L214】【F:backend/storage.py†L38-L79】

---

## 4) Ejemplos adicionales

### 4.1 Obtener scores para calibración manual
```bash
curl -X POST http://127.0.0.1:8000/infer \
  -F role_id=Master1 \
  -F roi_id=Pattern \
  -F mm_per_px=0.20 \
  -F image=@datasets/Master1/Pattern/ng/sample_ng.png \
  -F shape='{"kind":"rect","x":0,"y":0,"w":384,"h":384}' | jq '.score'
```

### 4.2 Reentrenar con muestras adicionales
Repite `/fit_ok` con nuevas imágenes; la memoria y el índice se sobrescriben con los embeddings actualizados.

### 4.3 Limpiar artefactos
```bash
rm -rf backend/models/Master1/Pattern
```
La siguiente llamada a `/infer` devolverá `400` indicando que falta memoria.

---

## 5) Convenciones generales

- Las imágenes se decodifican con OpenCV (`cv2.imdecode`) en BGR `uint8` antes de extraer embeddings.【F:backend/app.py†L46-L71】
- El extractor reescala internamente al `input_size` configurado (448 por defecto) y devuelve `token_shape` (`Ht`, `Wt`).
- El score global usa percentil configurable (99 por defecto) y se recalcula en `InferenceEngine.run` cada vez.【F:backend/infer.py†L80-L120】
- Las respuestas de error siempre incluyen `{ "error": "mensaje", "trace": "stacktrace" }` en 4xx/5xx cuando el backend captura excepciones.【F:backend/app.py†L116-L214】

Para detalles adicionales sobre formatos y almacenamiento consulta [DATA_FORMATS.md](DATA_FORMATS.md) y [backend/README_backend.md](backend/README_backend.md).
