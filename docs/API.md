# API del Backend

La API se expone vía FastAPI (`backend/app.py`) y sigue un contrato REST estable. Todas las rutas devuelven JSON y utilizan códigos HTTP estándar (`200` éxito, `4xx` errores de validación, `5xx` fallos inesperados). Los ejemplos están en español y mantienen el naming original para evitar romper integraciones existentes.

## Endpoints disponibles

| Método | Ruta            | Descripción breve                                      |
|--------|-----------------|--------------------------------------------------------|
| GET    | `/`             | Healthcheck simple (`{"status": "ok"}`)             |
| GET    | `/train/status` | Estado de colas de entrenamiento y último fit         |
| POST   | `/fit_ok`       | Añade muestras etiquetadas como OK                    |
| POST   | `/send_ng`      | Añade muestras etiquetadas como NG                    |
| POST   | `/predict`      | Ejecuta inferencia sobre una ROI                      |

### Campos comunes
- `role_id` (`string`): Identificador de la célula/rol en ejecución (`Inspection`, `Master`, etc.).
- `roi_id` (`string`): Identificador de la ROI (ej. `Inspection_1`).
- `mm_per_px` (`float`): Escala física en milímetros por píxel.
- `memory_fit` (`bool` textual): `"true"` para mantener embeddings en memoria tras el fit, `"false"` o ausente para persistir sin cargar.
- `images` (`File[]`): Lista de imágenes de ROI en formato PNG/JPG. En `/predict` se usa `image` singular.

## Ejemplo de uso con `curl`
```bash
# Alta de muestras OK
curl -X POST http://localhost:8000/fit_ok \
  -F "role_id=Inspection" \
  -F "roi_id=Inspection_1" \
  -F "mm_per_px=0.023" \
  -F "memory_fit=true" \
  -F "images=@sample1.png" -F "images=@sample2.png"

# Alta de muestras NG
curl -X POST http://localhost:8000/send_ng \
  -F "role_id=Inspection" \
  -F "roi_id=Inspection_2" \
  -F "mm_per_px=0.023" \
  -F "images=@defecto1.png"

# Inferencia
curl -X POST http://localhost:8000/predict \
  -F "role_id=Inspection" \
  -F "roi_id=Inspection_1" \
  -F "mm_per_px=0.023" \
  -F "image=@nueva.png"
```

## Respuestas esperadas
### GET `/train/status`
```json
{
  "status": "idle",
  "last_train_ts": "2025-10-20T10:21:45Z",
  "queue_size": 0,
  "model": "dino_v2",
  "device": "cuda:0",
  "coreset_size": 2048
}
```

### POST `/fit_ok`
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

### POST `/send_ng`
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

### POST `/predict`
```json
{
  "role_id": "Inspection",
  "roi_id": "Inspection_1",
  "scores": [
    {"roi": "Inspection_1", "score": 0.12, "is_anomaly": false, "threshold": 0.45}
  ],
  "inference_ms": 22.7,
  "image_resized": [2048, 2048]
}
```

## Manejo de errores
- **400 Bad Request**: campos faltantes, formatos inválidos (`mm_per_px` no numérico, imagen corrupta, etc.). FastAPI devuelve un JSON con `detail` explicando cada error.
- **409 Conflict**: inserciones duplicadas detectadas manualmente (poco habitual si los nombres de archivo son únicos).
- **500 Internal Server Error**: fallo inesperado durante inferencia o escritura. Revisar logs (`app.logger`) para detalle.

## Consejos de integración
- Enviar `roi_id` y `role_id` exactamente como se guardan en la GUI para mantener la persistencia coherente.
- Para cargas masivas, agrupar imágenes en una única petición (`images` repetido) reduce overhead HTTP.
- Usar cabeceras `Accept: application/json` y `Accept-Language: es` si se necesita localizar mensajes (aunque por defecto se devuelve JSON neutro).
- La documentación OpenAPI está disponible en `/docs` (Swagger) y `/redoc`.

## Versionado
El backend puede anunciar una versión mediante encabezado `X-BDI-Version` o en el payload de `/train/status` (`model_version`). Cualquier cambio de contrato debe reflejarse aquí y en `agents.md`.
