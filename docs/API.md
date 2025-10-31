# API del Backend

La API se expone vía FastAPI (`backend/app.py`) y sigue un contrato REST estable. Todas las rutas devuelven JSON y utilizan códigos HTTP estándar (`200` éxito, `4xx` errores de validación, `5xx` fallos inesperados). Los ejemplos mantienen nombres alineados con la GUI.

## Endpoints disponibles

| Método | Ruta            | Descripción breve                                            |
|--------|-----------------|--------------------------------------------------------------|
| GET    | `/health`       | Estado del servicio, dispositivo y versión del modelo        |
| POST   | `/fit_ok`       | Construye/actualiza memoria PatchCore con muestras OK        |
| POST   | `/calibrate_ng` | Calcula y persiste umbral a partir de scores OK/NG           |
| POST   | `/infer`        | Ejecuta inferencia PatchCore y devuelve score + heatmap      |

### Campos comunes
- `role_id` (`string`): Identificador de la célula/rol (`Inspection`, `Master`, etc.).
- `roi_id` (`string`): Identificador de la ROI (ej. `Inspection_1`).
- `mm_per_px` (`float`): Escala física en milímetros por píxel.
- `memory_fit` (`bool` textual): `"true"` para usar el 100% de embeddings en la llamada a `/fit_ok`.
- `images` (`File[]`): Lista de imágenes de ROI en formato PNG/JPG (solo en `/fit_ok`).
- `image` (`File`): Imagen individual enviada a `/infer`.
- `shape` (`string JSON`, opcional): Máscara rect/circle/annulus en coordenadas de la ROI (solo `/infer`).

## Ejemplo de uso con `curl`
```bash
# Alta de muestras OK (dos imágenes)
curl -X POST http://localhost:8000/fit_ok \
  -F "role_id=Inspection" \
  -F "roi_id=Inspection_1" \
  -F "mm_per_px=0.023" \
  -F "images=@sample1.png" -F "images=@sample2.png"

# Calibración NG a partir de scores
temp_json=$(mktemp)
cat <<'JSON' > "$temp_json"
{
  "role_id": "Inspection",
  "roi_id": "Inspection_1",
  "mm_per_px": 0.023,
  "ok_scores": [12.1, 10.8, 11.5],
  "ng_scores": [28.4],
  "area_mm2_thr": 1.0,
  "score_percentile": 99
}
JSON
curl -X POST http://localhost:8000/calibrate_ng \
  -H "Content-Type: application/json" \
  -d @"$temp_json"
rm "$temp_json"

# Inferencia con máscara circular
curl -X POST http://localhost:8000/infer \
  -F "role_id=Inspection" \
  -F "roi_id=Inspection_1" \
  -F "mm_per_px=0.023" \
  -F "image=@nueva.png" \
  -F "shape={\"kind\":\"circle\",\"cx\":192,\"cy\":192,\"r\":180}"
```

## Respuestas esperadas
### GET `/health`
```json
{
  "status": "ok",
  "device": "cpu",
  "model": "vit_small_patch14_dinov2.lvd142m",
  "version": "0.1.0"
}
```

### POST `/fit_ok`
```json
{
  "n_embeddings": 34992,
  "coreset_size": 700,
  "token_shape": [32, 32],
  "coreset_rate_requested": 0.02,
  "coreset_rate_applied": 0.018
}
```

### POST `/calibrate_ng`
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

### POST `/infer`
```json
{
  "role_id": "Inspection",
  "roi_id": "Inspection_1",
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

## Manejo de errores
- **400 Bad Request**: campos faltantes o formatos inválidos (`mm_per_px` no numérico, imagen corrupta, etc.). FastAPI devuelve un JSON con `detail` y mensaje.
- **404 Not Found**: `/infer` sin memoria previa para el par (`role_id`, `roi_id`).
- **500 Internal Server Error**: fallo inesperado durante inferencia o escritura. Revisar logs (`backend.app` logger) para detalle.

## Consejos de integración
- Enviar `roi_id` y `role_id` exactamente como se guardan en la GUI para mantener la persistencia coherente.
- Para cargas masivas, agrupar imágenes en una única petición (`images` repetido) reduce overhead HTTP.
- Usar cabeceras `Accept: application/json` para respuestas y revisar `/docs` o `/redoc` para el esquema OpenAPI actualizado.

## Versionado
`GET /health` expone el campo `version`. Cualquier cambio de contrato debe reflejarse aquí y en `agents.md`.
