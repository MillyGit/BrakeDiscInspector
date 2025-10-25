BrakeDiscInspector

BrakeDiscInspector es una solución de inspección visual para discos de freno con:

Frontend WPF (.NET) para crear/gestionar ROIs y ejecutar inferencias.

Backend FastAPI (Python) para extracción de características y scoring con modelos PatchCore/DINOv2.

Qué hay de nuevo (Q4 2025)

Flujo UI simplificado: primero cargar imagen, luego cargar preset (no auto). Eliminado “Load Models”.

Presets sin inspections: el preset guarda solo Masters y ajustes globales; las Inspection ROIs (1–4) se definen después.

Cuatro ROIs de inspección opcionales: Inspection 1..4. Puedes usar 1, 2, 3 o 4.

Freeze/Editar ROI: botón único que alterna entre guardar/congelar y editar (adorners).

Recolocado robusto: los Masters (y sus inspections ligados) se reubican y rotan al analizar; las Inspection ROIs (1–4) se mueven/rotan sólo si no están congeladas.

Datasets por ROI: cada ROI tiene su dataset OK/NG, su “train memory fit”, “calibrate threshold” y “evaluate”.

Miniaturas reales: las miniaturas muestran el recorte exacto del ROI (square, circle o annulus).

Logging reducido: menos ruido por defecto; logs clave en análisis/carga.

Arquitectura

GUI (WPF): gui/BrakeDiscInspector_GUI_ROI con MVVM ligero (MainWindow.xaml, MainWindow.xaml.cs, Workflow/WorkflowViewModel.cs, MasterLayout.cs, RoiModel.cs).

Backend: backend/ (FastAPI) con endpoints /infer, /fit_ok, /health, etc.

Datos: estructura de datasets por ROI y carpeta fija de presets fuera del dataset (ver DATA_FORMATS.md).

Requisitos rápidos

Windows 10/11, .NET 6+ (o el que use el proyecto), Python 3.10+.

Modelos/weights gestionados por backend (ver Prompt_Backend_PatchCore_DINOv2.md).

Uso básico

Cargar imagen (botón visible arriba-izquierda).

Cargar preset desde carpeta presets/ (diálogo de archivo). El preset no incluye inspections 1–4.

Definir Inspection ROIs 1–4 (opcionales) y pulsar Guardar ROI (congela). Alternar con Editar ROI para retocar.

Analyze Master para recolocar Masters e inspección ligada (si no está congelada) según las cruces y ángulos.

Para cada Inspection ROI activo: Add to OK / NG (con imagen actual), Train memory fit, Calibrate threshold, Evaluate.

Documentación adicional

ARCHITECTURE.md — capas y componentes.

API_REFERENCE.md — endpoints backend con ejemplos.

DATA_FORMATS.md — estructura de carpetas, presets y snapshots ROI.

ROI_AND_MATCHING_SPEC.md — geometrías, transformaciones y reposicionado.

DEV_GUIDE.md, DEPLOYMENT.md, LOGGING.md, CONTRIBUTING.md.

instructions_codex_gui_workflow.md — cómo usar Codex con prompts de este repo.
