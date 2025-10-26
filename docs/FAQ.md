# Preguntas Frecuentes (FAQ)

**¿Dónde se guardan las imágenes de OK/NG?**  
Por defecto en `data/rois/Inspection_<n>/{ok|ng}`. Si la ROI no tiene `DatasetPath` definido, se utiliza `data/datasets/<roiId>/{ok|ng}` como fallback.

**¿Qué guarda el archivo JSON junto a cada imagen?**  
Metadatos básicos: `role_id`, `roi_id`, `mm_per_px`, `timestamp_utc`, `source` y campos opcionales como `operator_id` o notas.

**¿Qué hace `memory_fit`?**  
Indica al backend que mantenga estructuras internas (coreset) en memoria para entrenamiento incremental rápido. Si es `false`, se persiste en disco y se recarga bajo demanda.

**¿Cómo activo GPU para el backend?**  
Instala las ruedas CUDA de PyTorch (ver [docs/SETUP.md](SETUP.md)) y verifica con `python -c "import torch; print(torch.cuda.is_available())"`. Asegúrate de tener drivers NVIDIA y CUDA 12.1 compatibles.

**¿Qué devuelve `POST /predict`?**  
Un JSON con `score` por ROI, `is_anomaly`, tiempo de inferencia y, si está habilitado, heatmaps base64. Puede incluir `threshold` y `regions` con áreas en px/mm².

**¿Se pueden enviar varias imágenes en una sola petición?**  
Sí, tanto `/fit_ok` como `/send_ng` aceptan múltiples archivos (`images=@...`). Reduce el número de llamadas y acelera la subida de datasets.

**¿Cómo reentreno el modelo?**  
Desde la GUI, añade nuevas muestras OK/NG y pulsa `Train memory fit`. Para reentrenos completos offline, asegúrate de que el backend tenga acceso a todos los datos y reinícialo para reconstruir el coreset si es necesario.

**¿Qué hacer si el backend tarda en iniciar por primera vez?**  
La primera carga del modelo DINOv2 puede tardar unos segundos (descarga de weights, compilación JIT). Revisa los logs y espera a que se muestre `Model ready` antes de ejecutar peticiones.

**¿Dónde encuentro documentación más detallada?**  
Consulta:
- [docs/ARQUITECTURA.md](ARQUITECTURA.md) para visión global.
- [docs/BACKEND.md](BACKEND.md) para detalles de implementación Python.
- [docs/GUI.md](GUI.md) para interacciones WPF.
- [docs/PIPELINE_DETECCION.md](PIPELINE_DETECCION.md) para conocer el flujo PatchCore.

**¿Cómo reporto un bug?**  
Abre un issue en GitHub describiendo el problema, versión, logs relevantes y pasos para reproducirlo. Adjunta capturas o archivos si procede.
