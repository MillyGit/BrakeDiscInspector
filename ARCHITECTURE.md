Arquitectura
Visión general

Frontend (WPF/.NET) — Dibujo y gestión de ROIs, presets, datasets y evaluación de resultados.

Backend (FastAPI/Python) — Extracción de features (DINOv2), memoria PatchCore y scoring.

Frontend

MainWindow.xaml — Maquetación principal por pestañas con foco en Inspection ROIs.

MainWindow.xaml.cs — Orquestación UI, wiring de eventos, dibujo en CanvasROI, redibujado de overlays y llamadas al VM.

Workflow/WorkflowViewModel.cs — Estado de la sesión: imagen actual, selección de ROI, flags de overlays, comandos (load image, load preset, analyze master, add-to-ok/ng, train, calibrate, evaluate), datasets.

MasterLayout.cs — Modelo cargado en preset: Master1, Master1Inspection, Master2, Master2Inspection y ajustes globales (scale_lock, use_local_matcher, mm_per_px, etc.). NO incluye Inspections 1–4.

RoiModel.cs — ROI genérico: Id, Name, Shape (square|circle|annulus), Center, Size/Radius/InnerRadius, RotationDeg, IsFrozen (default true, en memoria), snapshots, etc.

Backend

FastAPI con endpoints:

GET /health

POST /infer — devuelve score, heatmap opcional, threshold (nullable), regions.

POST /fit_ok — entrena la memoria OK (evitar CORS preflight 405; ver API_REFERENCE.md).

Pipeline: preprocesado 448×448, patch=14, grid 32×32, tokens=1025, embeddings; memoria PatchCore; scoring y mapa de calor.

Reposicionado y matching

Analyze Master calcula offset y rotación usando patrones (cruces) de Master1 y Master2.

Aplicación de transformaciones:

A Master1/2 y a Master1/2 Inspection siempre.

A Inspection 1–4 solo si IsFrozen == false (si están congelados, permanecen fijos).

Centro shape-aware:

square: centro del rectángulo.

circle: centro del círculo, radio = Size/2.

annulus: centro común, con InnerRadius usado para recortes/miniaturas y enmascarado.

Miniaturas

Generadas desde la imagen actual aplicando la geometría real. Annulus recorta el anillo, no el disco completo. Ver DATA_FORMATS.md para rutas.
