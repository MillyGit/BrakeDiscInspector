# DATA_FORMATS — BrakeDiscInspector

Este documento resume los formatos de datos utilizados entre la GUI y el backend PatchCore (FastAPI), así como los archivos generados en disco.

---

## 1) Requests al backend

### 1.1 `POST /fit_ok`

- **Tipo**: `multipart/form-data`
- **Campos**:
  | Campo | Tipo | Descripción |
  |-------|------|-------------|
  | `role_id` | string | Identificador del rol o layout actual. |
  | `roi_id` | string | Identificador de la ROI dentro del rol. |
  | `mm_per_px` | float | Resolución usada para reportar áreas. |
  | `images[]` | fichero(s) | PNG/JPG del ROI canónico (recortado + rotado). |

### 1.2 `POST /calibrate_ng`

- **Tipo**: `application/json`
- **Esquema**:
  ```json
  {
    "role_id": "Master1",
    "roi_id": "Pattern",
    "mm_per_px": 0.2,
    "ok_scores": [12.1, 10.8, 11.5],
    "ng_scores": [28.4],
    "area_mm2_thr": 1.0,
    "score_percentile": 99
  }
  ```
- `ng_scores` es opcional. Si está vacío, el backend usa `p99(ok_scores)` como umbral.

### 1.3 `POST /infer`

- **Tipo**: `multipart/form-data`
- **Campos**:
  | Campo | Tipo | Descripción |
  |-------|------|-------------|
  | `role_id` | string | Debe coincidir con el usado en `/fit_ok`. |
  | `roi_id` | string | Debe coincidir con el usado en `/fit_ok`. |
  | `mm_per_px` | float | Resolución para calcular áreas mm². |
  | `image` | fichero | PNG/JPG del ROI canónico. |
  | `shape` | string JSON (opcional) | Máscara (`rect`, `circle`, `annulus`) en coordenadas del ROI. |

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
    "coreset_rate": 0.02,
    "score_percentile": 99,
    "mm_per_px": 0.2
  }
}
```

- `heatmap_png_base64`: PNG de 8 bits (grises) del heatmap.
- `regions`: lista ordenada por área descendente tras aplicar `area_mm2_thr` y filtrado morfológico.

### 2.4 `/health`
```json
{
  "status": "ok",
  "device": "cuda",
  "model": "vit_small_patch14_dinov2.lvd142m",
  "version": "0.1.0"
}
```

**Errores**: cualquier endpoint puede responder `{ "error": "mensaje", "trace": "stacktrace" }` con códigos 4xx/5xx.

---

## 3) Archivos generados en disco

### 3.1 Persistencia del backend (`backend/models/<role>/<roi>/`)

| Archivo | Tipo | Descripción |
|---------|------|-------------|
| `memory.npz` | NumPy NPZ | Contiene `emb` (coreset), `token_h`, `token_w` y metadata (`coreset_rate`, `applied_rate`). |
| `index.faiss` | Binario | Índice FlatL2 serializado (opcional; se crea si FAISS está disponible). |
| `calib.json` | JSON | Resultado de `/calibrate_ng` con umbral y parámetros persistidos. |

### 3.2 Dataset local de la GUI (`datasets/<role>/<roi>/<ok|ng>/`)

| Archivo | Contenido |
|---------|-----------|
| `*.png` | ROI canónico exportado por la GUI (PNG 8/24 bits). |
| `*.json` | Metadata asociada: `role_id`, `roi_id`, `mm_per_px`, `shape`, `source_path`, `angle`, `timestamp`. |

### 3.3 Heatmaps temporales

- La GUI decodifica `heatmap_png_base64` a `byte[]` y lo muestra sin persistir por defecto.
- Si se decide guardar evidencias, se recomienda carpeta `evidence/<fecha>/<role>/<roi>/heatmap_<timestamp>.png` (fuera del repo).

---

## 4) Modelos de datos en la GUI (C#)

```csharp
public record BackendRegion(
    Rect BBox,
    double AreaPx,
    double AreaMm2,
    IReadOnlyList<Point> Contour
);

public record InferResult(
    string RoleId,
    string RoiId,
    double Score,
    double Threshold,
    byte[] HeatmapPng,
    IReadOnlyList<BackendRegion> Regions,
    int TokenHeight,
    int TokenWidth
);

public record FitOkResult(int NEmbeddings, int CoresetSize, int TokenHeight, int TokenWidth);

public record CalibrateResult(
    double Threshold,
    double? P99Ok,
    double? P5Ng,
    double MmPerPx,
    double AreaMm2Thr,
    int ScorePercentile
);
```

Las estructuras anteriores deben mantenerse sincronizadas con los contratos documentados.

---

## 5) Convenciones generales

- Todas las coordenadas (`bbox`, contornos) se expresan en píxeles del ROI canónico.
- `mm_per_px` siempre acompaña a las operaciones para convertir áreas a mm².
- Los ficheros JSON deben guardarse en UTF-8 sin BOM.
- El backend acepta PNG/JPG; la GUI recomienda PNG para evitar pérdidas de calidad.

Consulta [API_REFERENCE.md](API_REFERENCE.md) para ejemplos detallados de llamadas HTTP y [ARCHITECTURE.md](ARCHITECTURE.md) para el flujo end-to-end.
