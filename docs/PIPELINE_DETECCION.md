# Pipeline de Detección de Anomalías

## Resumen del flujo
El pipeline PatchCore+DINOv2 procesa cada ROI canónica enviada por la GUI siguiendo pasos bien definidos. El objetivo es mantener latencia baja, reproducibilidad y capacidad de reentrenar con nuevas muestras.

1. **Recepción de imagen**: FastAPI recibe la imagen vía `multipart/form-data`, la valida (extensión, tamaño, modo de color) y la convierte en tensor `torch.FloatTensor` normalizado en `[0, 1]` manteniendo la relación `mm_per_px`.
2. **Normalización / resizing**: si la resolución excede los límites soportados (p.ej., >12 MP), se aplica un reescalado controlado que preserva la escala física (ajustando `mm_per_px`). Para resoluciones estándar (2–4 MP) no se modifica.
3. **Extracción de features**: `features.py` inicializa el backbone DINOv2 (ViT-S/14) y genera embeddings por token. Se soporta ejecución en GPU (`cuda`) o CPU según disponibilidad. Los embeddings se normalizan (`L2`) para facilitar comparaciones.
4. **Selección / actualización de coreset**: cuando se realiza `fit_ok`, los embeddings se almacenan y se construye el coreset PatchCore (k-center greedy). Con `memory_fit=true`, se conserva la muestra completa para escenarios de recalibración offline.
5. **Scoring**: durante `infer`, se calcula la distancia de cada token al coreset. Se obtiene un mapa de anomalía y se agregan estadísticas (percentil p99 por defecto) dentro de la máscara de ROI.
6. **Decisión**: se compara el score con el umbral vigente (derivado de `/calibrate_ng` o valores por defecto). Se devuelven `score`, `threshold`, `heatmap` y regiones destacadas.
7. **Salida opcional**: si está habilitado, se genera un heatmap coloreado (`PNG` en base64) recortado a la ROI canónica para visualizar zonas calientes.

## Consideraciones de rendimiento
- **Latencia**: En GPU NVIDIA con CUDA 12.1, la inferencia típica es 20–40 ms por ROI de 2–4 MP. Para imágenes de 12 MP, se recomienda preprocesado en la GUI (downscale consistente) o activar batching.
- **Batching**: `/fit_ok` puede recibir múltiples imágenes y procesarlas en lote, reduciendo overhead de inicialización.
- **Precarga de modelo**: `features.py` mantiene la instancia del modelo en caché. Primeras llamadas pueden ser más lentas (compilación JIT, warmup).
- **Uso de memoria**: `memory_fit=true` conserva embeddings en RAM para entrenamientos incrementales; si la memoria es limitada, se puede desactivar para persistir únicamente en disco y recargar bajo demanda.

## Umbrales y calibración
- Los umbrales se pueden derivar automáticamente (percentil 99 de scores OK) o manualmente a partir de muestras NG.
- Para calibraciones, se recomienda ejecutar inferencias sobre un set de validación NG y utilizar el endpoint correspondiente (si está habilitado) o ajustar desde la GUI.
- `mm_per_px` se utiliza para convertir áreas de heatmap a mm² cuando se requiere reporte físico.

## Persistencia
- Cada `fit_ok` guarda `models/<role,roi>.npz` con embeddings y metadatos (`coreset_rate`, `token_shape`).
- `/calibrate_ng` genera `models/<role,roi>_calib.json` con umbrales y parámetros (`mm_per_px`, percentil, área mínima).
- Se mantiene un índice FAISS opcional para acelerar reinicios. Durante el arranque, el backend puede reconstruir el coreset leyendo los archivos almacenados.

## Debug y logging
- El backend registra tiempos de inferencia, tamaño de lotes y dispositivo (`cuda`/`cpu`).
- Para depurar heatmaps se puede habilitar un flag que guarde las imágenes generadas en `data/debug/`.
- Los tests en `backend/tests` verifican componentes ligeros; para pruebas integrales se sugiere usar scripts en `scripts/` (cuando estén disponibles).
