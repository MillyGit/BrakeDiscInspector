Backend — PatchCore + DINOv2
Flujo

fit_ok con imágenes OK para poblar memoria.

infer devuelve score, threshold y heatmap opcional.

calibrate (si existe) con NG para ajustar threshold. Si no, el frontend gestiona umbrales locales.

Notas

Preproc: 448×448, patch 14, grid 32×32, tokens 1025.

Almacenar model_key por ROI.
