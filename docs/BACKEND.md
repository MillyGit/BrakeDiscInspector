# Backend FastAPI

## Estructura del backend
El backend reside en `backend/` y está construido sobre FastAPI. Los módulos principales son:

- `app.py`: instancia FastAPI y define los endpoints `/health`, `/fit_ok`, `/calibrate_ng` y `/infer`. Orquesta el extractor DinoV2, la memoria PatchCore y la persistencia en disco.
- `features.py`: inicializa el extractor DINOv2 ViT-S/14 (vía `timm` + `torch`). Implementa carga perezosa cuando `BDI_LIGHT_IMPORT=1` para mantener el CI ligero.
- `patchcore.py`: funciones auxiliares para coreset k-center greedy y búsqueda kNN (FAISS o sklearn).
- `infer.py`: lógica de inferencia PatchCore (distancias → heatmap → score/regions).
- `calib.py`: selección de umbral a partir de puntuaciones OK/NG.
- `roi_mask.py`: utilidades para generar máscaras de ROI (rect, circle, annulus) en espacio canónico.
- `storage.py`: gestiona archivos persistentes en `models/` (`*.npz`, `*_index.faiss`, `*_calib.json`).
- `utils.py`: funciones de soporte (normalización, manejo de base64, serialización JSON).
- `tests/`: pruebas unitarias ligeras para `/health`, `/fit_ok` y `/calibrate_ng` usando `fastapi.testclient`.

## Dependencias y configuración
- Las dependencias están fijadas en `backend/requirements.txt` (FastAPI, Uvicorn, PyTorch CPU, OpenCV, NumPy, scikit-learn, etc.).
- Variables relevantes:
  - `BDI_MODELS_DIR`: ruta donde se guardan memorias/calibraciones (por defecto `models/`).
  - `BDI_LIGHT_IMPORT=1`: evita cargar `timm/torchvision` durante importaciones en CI.
  - `DEVICE`, `INPUT_SIZE`, `CORESET_RATE` se pueden ajustar vía `backend/config.py`.

## Modos de comunicación
Las peticiones hacia el backend combinan JSON y archivos binarios según el endpoint:

- **`GET /health`** — JSON simple para estado del servicio.
- **`POST /fit_ok`** — `multipart/form-data` con múltiples imágenes OK (ROI canónico) para construir memoria PatchCore.
- **`POST /calibrate_ng`** — `application/json` con puntuaciones OK/NG para fijar umbral.
- **`POST /infer`** — `multipart/form-data` con imagen ROI + máscara opcional (`shape` JSON) para obtener score, threshold y heatmap.

## Esquemas de JSON de referencia
### `GET /health`
```json
{
  "status": "ok",
  "device": "cpu",
  "model": "vit_small_patch14_dinov2.lvd142m",
  "version": "0.1.0"
}
```

### `POST /fit_ok`
Campos del formulario:
- `role_id` (`str`)
- `roi_id` (`str`)
- `mm_per_px` (`float`)
- `memory_fit` (`bool` textual: `"true"|"false"`, opcional)
- `images` (`1..N` archivos PNG/JPG del ROI canónico)

Respuesta típica:
```json
{
  "n_embeddings": 34992,
  "coreset_size": 700,
  "token_shape": [32, 32],
  "coreset_rate_requested": 0.02,
  "coreset_rate_applied": 0.018
}
```

### `POST /calibrate_ng`
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

### `POST /infer`
```json
{
  "role_id": "Master1",
  "roi_id": "Pattern",
  "score": 18.7,
  "threshold": 20.0,
  "heatmap_png_base64": "iVBORw0K...",
  "regions": [
    {"bbox": [x, y, w, h], "area_px": 250.0, "area_mm2": 10.0, "contour": [[x1, y1], ...] }
  ],
  "token_shape": [32, 32],
  "params": {
    "extractor": "vit_small_patch14_dinov2.lvd142m",
    "input_size": 448,
    "coreset_rate": 0.02,
    "score_percentile": 99
  }
}
```

## Rutas de guardado
- **Memorias**: `models/<encoded-role>__<encoded-roi>.npz` con embeddings y token grid.
- **Índices**: `models/<encoded-role>__<encoded-roi>_index.faiss` (opcional si FAISS está disponible).
- **Calibraciones**: `models/<encoded-role>__<encoded-roi>_calib.json` con umbrales y metadatos.

## Ejecución local
```bash
cd backend
uvicorn app:app --host 0.0.0.0 --port 8000 --reload
```
Esto expone la documentación interactiva en `http://localhost:8000/docs`.

## Buenas prácticas
- Mantener los endpoints y campos alineados con `agents.md` para evitar regresiones.
- Ejecutar `pytest backend/tests` en CI antes de desplegar cambios.
- Registrar tiempos clave (extracción, coreset, inferencia) usando el logger configurado en `app.py`.
- Validar siempre que la GUI envíe ROIs canónicas (recortadas + rotadas); el backend no modifica geometría.
