# Preguntas Frecuentes (FAQ)

**¿Dónde se guardan las memorias y calibraciones?**
En `models/`, codificando `role_id` y `roi_id` en los nombres de archivo. Ejemplos:
- `models/<role>__<roi>.npz` para la memoria PatchCore (embeddings + token grid).
- `models/<role>__<roi>_calib.json` para umbrales y percentiles.
- `models/<role>__<roi>_index.faiss` si se generó un índice FAISS opcional.

**¿Qué información adicional se almacena?**
Los `.npz` incluyen `coreset_rate` aplicado, mientras que los `.json` guardan `threshold`, `mm_per_px`, `score_percentile` y `area_mm2_thr`. No se almacenan imágenes OK/NG en esta versión del backend; la GUI conserva los datasets originales.

**¿Qué hace `memory_fit`?**
Permite forzar que el coreset use el 100% de los embeddings enviados en esa llamada (útil para recalibraciones rápidas). Si es `false`, se aplica el `coreset_rate` configurado para reducir memoria y disco.

**¿Cómo activo GPU para el backend?**
Instala las ruedas CUDA de PyTorch (ver [docs/SETUP.md](SETUP.md)) y verifica con `python -c "import torch; print(torch.cuda.is_available())"`. Asegúrate de tener drivers NVIDIA y CUDA 12.1 compatibles.

**¿Qué devuelve `POST /infer`?**
Un JSON con `score`, `threshold`, `heatmap_png_base64`, `regions` y `token_shape`. Incluye metadatos (`extractor`, `input_size`, `coreset_rate`, `score_percentile`) para depuración.

**¿Se pueden enviar varias imágenes en una sola petición?**
Sí, `/fit_ok` acepta múltiples archivos (`images=@...`) y procesa cada ROI en lote. `/infer` espera una única imagen y `/calibrate_ng` recibe puntuaciones en JSON.

**¿Cómo reentreno el modelo?**
Desde la GUI, añade nuevas muestras OK y vuelve a llamar a `/fit_ok`. Si necesitas recalcular el umbral con NG, ejecuta inferencias para obtener scores y luego invoca `/calibrate_ng`.

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
