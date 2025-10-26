# Backend FastAPI

## Estructura del backend
El backend reside en `backend/` y está construido sobre FastAPI. Los módulos principales son:

- `app.py`: crea la instancia FastAPI, define los endpoints (`/`, `/train/status`, `/fit_ok`, `/send_ng`, `/predict`), valida campos de formularios `multipart/form-data`, serializa respuestas JSON y coordina workers de entrenamiento. Respeta la variable `memory_fit` para decidir si se actualiza un coreset en memoria.
- `features.py`: encapsula la inicialización del modelo (DINOv2 ViT-S/14 vía `timm`). Implementa **lazy import** de dependencias pesadas (`timm`, `torchvision`) cuando la variable de entorno `BDI_LIGHT_IMPORT=1` está activa, lo cual mantiene el CI ligero. En ejecución real, detecta GPU (`torch.cuda.is_available()`) y desplaza el modelo al dispositivo adecuado.
- `dataset_manager.py`: organiza las muestras OK/NG en disco utilizando carpetas por `(role_id, roi_id)`. Genera nombres de archivo únicos con timestamp y guarda un JSON de metadatos con información como `mm_per_px`, `source`, `timestamp_utc` y el identificador de la ROI. Gestiona también la lectura y conteo de muestras para reportar tamaños de datasets.
- `tests/`: contiene pruebas unitarias, entre ellas `test_app_train_status.py`, que valida que `/train/status` responde correctamente sin inicializar modelos pesados (se apoya en `BDI_LIGHT_IMPORT`).

## Dependencias y configuración
- Las dependencias están fijadas en `backend/requirements.txt` (FastAPI, Uvicorn, PyTorch, OpenCV, NumPy, etc.).
- Para entornos sin GPU o pipelines CI se recomienda exportar `BDI_LIGHT_IMPORT=1` y fijar Torch CPU (`torch==2.5.1+cpu`).
- El backend respeta variables como `BDI_DATA_ROOT` para personalizar el directorio raíz de datasets (por defecto `data/`).

## Modos de comunicación
Las peticiones hacia el backend combinan JSON y archivos binarios según el endpoint:

- **REST JSON** para respuestas y consultas (`GET /train/status`).
- **Multipart/form-data** para subir imágenes (`POST /fit_ok`, `POST /send_ng`, `POST /predict`).
- Los campos clave esperados en formularios son: `role_id`, `roi_id`, `mm_per_px`, `memory_fit` (opcional, bool textual), `images` (una o varias), y en el caso de predicción `image` singular.

## Esquemas de JSON de referencia
### `GET /train/status`
```json
{
  "status": "idle|running",
  "last_train_ts": "2025-10-20T10:21:45Z",
  "queue_size": 0,
  "model": "dino_v2",
  "device": "cuda:0|cpu",
  "coreset_size": 2048
}
```

### `POST /fit_ok`
Campos del formulario:
- `role_id` (`str`)
- `roi_id` (`str`)
- `mm_per_px` (`float`)
- `memory_fit` (`bool` textual: `"true"|"false"`)
- `images` (`1..N` archivos PNG/JPG del ROI)

Respuesta típica:
```json
{
  "ok_added": 3,
  "target_dir": "data/rois/Inspection_1/ok",
  "samples": [
    {
      "file": "data/rois/Inspection_1/ok/OK_20251026_121212345.png",
      "meta": "data/rois/Inspection_1/ok/OK_20251026_121212345.json"
    }
  ]
}
```

### `POST /send_ng`
```json
{
  "ng_added": 1,
  "target_dir": "data/rois/Inspection_1/ng",
  "samples": [
    {
      "file": "data/rois/Inspection_1/ng/NG_20251026_121342000.png",
      "meta": "data/rois/Inspection_1/ng/NG_20251026_121342000.json"
    }
  ]
}
```

### `POST /predict`
```json
{
  "role_id": "Inspection",
  "roi_id": "Inspection_1",
  "scores": [
    {"roi": "Inspection_1", "score": 0.12, "is_anomaly": false}
  ],
  "inference_ms": 22.7,
  "image_resized": [2048, 2048]
}
```

Los campos `scores` pueden incluir información adicional como `threshold`, `percentile` o regiones destacadas cuando se habilitan heatmaps.

## Rutas de guardado
- **Ruta principal**: `data/rois/Inspection_<n>/{ok|ng}` para ROIs definidas en inspección. Cada imagen guarda su JSON contiguo (`.json`).
- **Ruta secundaria (fallback)**: `data/datasets/<roiId>/{ok|ng}` para casos sin `DatasetPath` asignado desde la GUI.
- El backend crea las carpetas de forma perezosa (`os.makedirs(exist_ok=True)`) y evita sobrescribir archivos existentes generando sufijos únicos.

## Ejecución local
```bash
cd backend
uvicorn app:app --host 0.0.0.0 --port 8000 --reload
```
Esto expone la documentación interactiva en `http://localhost:8000/docs`.

## Buenas prácticas
- Mantener los endpoints y campos alineados con `agents.md` para evitar regresiones.
- Utilizar `async`/`await` en funciones que realizan I/O (lectura de disco, escritura, inferencia) para aprovechar el event loop.
- Registrar eventos relevantes (inicio/fin de entrenamiento, tamaño de datasets) usando el logger configurado en `app.py`.
- En entornos con GPU, verificar `torch.backends.cudnn.benchmark = True` para acelerar inferencia en tamaños fijos, sin romper determinismo cuando no se requiera.
