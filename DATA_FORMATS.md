# 📌 Actualización — 2025-10-07

**Cambios clave documentados en esta versión:**
- Requests/responses sincronizados con `backend/app.py` e `InferenceEngine.run` (score, threshold, heatmap, regions, token_shape).
- Esquemas de artefactos persistidos (`memory.npz`, `index.faiss`, `calib.json`) actualizados según `ModelStore`.
- Metadatos de datasets GUI ajustados a `Workflow/DatasetManager.cs` y `DatasetSample.cs` (`shape_json`, `mm_per_px`, `angle`).

# DATA_FORMATS — BrakeDiscInspector

Resumen de los formatos de datos intercambiados entre la GUI WPF y el backend FastAPI PatchCore, así como de los archivos generados en disco.

---

## Índice rápido

- [Requests al backend](#1-requests-al-backend)
- [Responses del backend](#2-responses-del-backend)
- [Archivos generados en disco](#3-archivos-generados-en-disco)
- [Modelos de datos en la GUI](#4-modelos-de-datos-en-la-gui)
- [Convenciones generales](#5-convenciones-generales)

---

## 1) Requests al backend

### 1.1 `POST /fit_ok`
- **Tipo**: `multipart/form-data`
- **Campos**
  | Campo | Tipo | Descripción |
  |-------|------|-------------|
  | `role_id` | string | Identificador del rol/preset. |
  | `roi_id` | string | Identificador de la ROI. |
  | `mm_per_px` | float | Resolución del ROI (mm por pixel). |
  | `images` | fichero(s) | PNG/JPG canónicos (crop + rotación ya aplicados). |

### 1.2 `POST /calibrate_ng`
- **Tipo**: `application/json`
- **Ejemplo**
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
- `ng_scores` es opcional. Si está vacío, el umbral resultante es `p99(ok_scores)`.

### 1.3 `POST /infer`
- **Tipo**: `multipart/form-data`
- **Campos**
  | Campo | Tipo | Descripción |
  |-------|------|-------------|
  | `role_id` | string | Debe coincidir con el usado en `/fit_ok`. |
  | `roi_id` | string | Debe coincidir con el usado en `/fit_ok`. |
  | `mm_per_px` | float | Necesario para convertir áreas a mm². |
  | `image` | fichero | ROI canónico (PNG/JPG). |
  | `shape` | string (opcional) | JSON con máscara (`rect`, `circle`, `annulus`) en píxeles del ROI. |

### 1.4 `GET /health`
- **Tipo**: `application/json`
- **Body**: vacío.

---

## 2) Responses del backend

### 2.1 `/fit_ok`
```json
{
  "n_embeddings": 34992,
  "coreset_size": 700,
  "token_shape": [32, 32],
  "coreset_rate_requested": 0.02,
  "coreset_rate_applied": 0.018
}
```

### 2.2 `/calibrate_ng`
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

### 2.3 `/infer`
```json
{
  "score": 18.7,
  "threshold": 20.0,
  "token_shape": [32, 32],
  "heatmap_png_base64": "iVBORw0K...",
  "regions": [
    {
      "bbox": [x, y, w, h],
      "area_px": 250.0,
      "area_mm2": 10.0,
      "contour": [[x1, y1], ...]
    }
  ]
}
```
- `threshold` puede ser `null` si no se ha calibrado.
- `regions` puede estar vacío si no se supera el umbral.
- El heatmap es un PNG en escala de grises (`uint8`).【F:backend/infer.py†L122-L181】

### 2.4 `/health`
```json
{
  "status": "ok",
  "device": "cuda",
  "model": "vit_small_patch14_dinov2.lvd142m",
  "version": "0.1.0"
}
```

### 2.5 Errores
Los endpoints pueden responder con códigos `4xx/5xx` y payload:
```json
{
  "error": "mensaje descriptivo",
  "trace": "stacktrace"
}
```
Esto se emite cuando `app.py` captura una excepción durante la petición.【F:backend/app.py†L108-L214】

---

## 3) Archivos generados en disco

### 3.1 Persistencia backend (`backend/models/<role>/<roi>/`)

| Archivo | Tipo | Contenido |
|---------|------|-----------|
| `memory.npz` | NumPy NPZ | `emb` (coreset `float32`), `token_h`, `token_w`, `metadata` JSON con tasas de coreset.【F:backend/storage.py†L12-L64】 |
| `index.faiss` | Binario | Índice serializado (si FAISS está disponible). |
| `calib.json` | JSON | Umbral (`threshold`), percentiles (`p99_ok`, `p5_ng`), `mm_per_px`, `area_mm2_thr`, `score_percentile`. |

### 3.2 Dataset GUI (`datasets/<role>/<roi>/<ok|ng>/`)

| Archivo | Contenido |
|---------|-----------|
| `*.png` | ROI canónico exportado por la GUI (PNG 8/24 bits). |
| `*.json` | Metadata serializada desde `DatasetManager`: `{ "role_id", "roi_id", "mm_per_px", "shape_json", "source_path", "angle", "timestamp" }`.【F:gui/BrakeDiscInspector_GUI_ROI/Workflow/DatasetManager.cs†L38-L74】【F:gui/BrakeDiscInspector_GUI_ROI/Workflow/DatasetSample.cs†L72-L120】 |
| `manifest.json` *(opcional)* | Resumen del dataset (contadores, fecha último `fit_ok`, versión GUI). |

### 3.3 Evidencias temporales
- La GUI decodifica `heatmap_png_base64` para mostrarlo; no se persiste por defecto.
- Se recomienda guardar heatmaps/contornos bajo `evidence/<fecha>/<role>/<roi>/` si se requiere auditoría.

---

## 4) Modelos de datos en la GUI

Ejemplo de DTOs utilizados en `Workflow` (simplificados):

```csharp
public sealed class FitOkResult
{
    public int n_embeddings { get; set; }
    public int coreset_size { get; set; }
    public int[]? token_shape { get; set; }
}

public sealed class CalibResult
{
    public double? threshold { get; set; }
    public double? p99_ok { get; set; }
    public double? p5_ng { get; set; }
    public double mm_per_px { get; set; }
    public double area_mm2_thr { get; set; }
    public int score_percentile { get; set; }
}

public sealed class InferRegion
{
    public double[]? bbox { get; set; }
    public double area_px { get; set; }
    public double area_mm2 { get; set; }
    public double[][]? contour { get; set; }
}

public sealed class InferResult
{
    public double score { get; set; }
    public double? threshold { get; set; }
    public string? heatmap_png_base64 { get; set; }
    public InferRegion[]? regions { get; set; }
    public int[]? token_shape { get; set; }
}
```

Ajustar los DTOs si el backend añade campos opcionales (`params`, `metadata`), manteniendo compatibilidad con JSON web (`JsonSerializerDefaults.Web`).

---

## 5) Convenciones generales

- Todos los tiempos se registran en UTC (`timestamp` ISO 8601) en metadatos GUI.
- `mm_per_px` debe mantenerse consistente entre dataset, entrenamiento y calibración para que `area_mm2` sea fiable.
- Las rutas se construyen con `Path.Combine` para evitar problemas multiplataforma.
- El backend espera imágenes BGR `uint8`; cualquier normalización/rotación debe realizarla la GUI antes de enviar la petición.【F:backend/app.py†L46-L71】
- Los archivos generados por el backend se deben versionar fuera del repositorio Git (carpetas montadas, unidades externas).

Para detalles sobre geometría y sincronización ROI ↔ heatmap consulta [ROI_AND_MATCHING_SPEC.md](ROI_AND_MATCHING_SPEC.md).
