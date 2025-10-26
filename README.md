# BrakeDiscInspector

## Resumen del proyecto
BrakeDiscInspector es una solución completa de inspección visual para discos de freno enfocada en detectar anomalías como faltas de pintura, doble taladrado, pegotes y otros defectos superficiales. Combina una GUI WPF (.NET) para la captura y gestión de Regiones de Interés (ROIs) con un backend de visión por computador basado en FastAPI, PyTorch y modelos tipo PatchCore/DINOv2. Sus objetivos principales son:

- **Latencia mínima** durante la inferencia gracias a la ejecución acelerada por GPU cuando está disponible.
- **Soporte de resoluciones variables** entre 2 y 12 MP sin perder la escala física (mm/px) de cada captura.
- **Reentrenos offline** y controlados por el operario, reutilizando las muestras almacenadas en disco.
- **ROIs manuales y configurables** (rectángulo, círculo, anillo) que se congelan o editan desde la GUI según el flujo de inspección.

## Arquitectura general
El sistema se organiza en dos capas principales que colaboran mediante llamadas HTTP (REST + multipart/form-data):

- **GUI (WPF .NET)**: proporciona herramientas de dibujo para definir ROIs canónicas, administra presets, datasets y comandos de entrenamiento/evaluación, y muestra overlays y miniaturas reales de cada ROI. Implementa MVVM ligero (`MainWindow`, `WorkflowViewModel`, controles específicos) y mantiene la experiencia del operario.
- **Backend (FastAPI + PyTorch/timm/OpenCV/Faiss)**: recibe imágenes recortadas desde la GUI, extrae embeddings con modelos preentrenados, gestiona coresets y datasets (OK/NG), expone endpoints de fit/predict/estado y persiste metadatos asociados.

Ambos componentes cumplen el contrato descrito en `agents.md`, preservando la exportación canónica de ROIs y la estabilidad de los endpoints.

## Estructura del repositorio
```
backend/
  app.py                  # FastAPI app (endpoints REST)
  features.py             # Extracción embeddings (timm/torch), lazy import
  dataset_manager.py      # Guardado/carga de muestras OK/NG + metadatos JSON
  requirements.txt        # Deps Python (FastAPI, Torch, OpenCV, etc.)
  tests/                  # Tests (unittest) p.ej. test_app_train_status.py
gui/
  BrakeDiscInspector_GUI_ROI/
    MainWindow.xaml       # Vista principal (WPF)
    MainWindow.xaml.cs    # Lógica de UI / eventos
    Workflow/WorkflowViewModel.cs      # VM principal: comandos/estados
    Controls/WorkflowControl.xaml      # Panel de dataset/acciones
    ...
docs/
  (documentación en profundidad; ver índice más abajo)
.github/workflows/
  ci.yml (o similar)      # CI: instalar deps e invocar tests del backend
```

### Otros archivos relevantes
- `ARCHITECTURE.md`, `API_REFERENCE.md`, `DATA_FORMATS.md`, `ROI_AND_MATCHING_SPEC.md`: especificaciones existentes sobre capas, endpoints históricos y geometrías.
- `DEV_GUIDE.md`, `DEPLOYMENT.md`, `LOGGING.md`, `CONTRIBUTING.md`: guías operativas complementarias.
- `Prompt_Backend_PatchCore_DINOv2.md`: decisiones de modelos y parámetros.
- `instructions_codex_gui_workflow.md`: pautas para agentes Codex.

## Flujo general de uso
1. El operario **carga una imagen** en la GUI y define una o varias **ROIs** manuales (Rect, Circle o Annulus).
2. Tras congelar el ROI (`Toggle Inspection` → `IsFrozen=true`), la ROI queda bloqueada hasta que el operario active “Editar ROI”.
3. La GUI envía la ROI canónica al **backend** mediante HTTP (FastAPI) para operaciones de `fit_ok`, `send_ng`, `predict`, estado de entrenamiento (`/train/status`), etc.
4. El backend extrae **features** (timm/torch), actualiza coresets/datasets y responde con JSON normalizados.
5. El operario **añade muestras** a carpetas **OK/NG**; los archivos (PNG) y sus metadatos JSON se guardan en disco (`data/rois/...`). La GUI refresca los thumbnails y contadores.
6. Los reentrenos se ejecutan **offline** bajo demanda utilizando las muestras existentes y el modo `memory_fit` para acelerar iteraciones.
7. En producción, se utiliza el modo **predict** para inspección con latencia baja (GPU cuando está disponible), devolviendo puntuaciones y flags de anomalía.

## Qué hay de nuevo (Q4 2025)
- Flujo UI simplificado: primero cargar imagen, luego cargar preset (sin auto). Eliminado “Load Models”.
- Presets sin inspections: el preset guarda solo Masters y ajustes globales; las Inspection ROIs (1–4) se definen después.
- Cuatro ROIs de inspección opcionales: `Inspection 1..4`. Se puede trabajar con 1, 2, 3 o 4 según la célula.
- Freeze/Editar ROI: botón único que alterna entre guardar/congelar y editar (adorners).
- Recolocado robusto: los Masters (y sus inspections ligados) se reubican y rotan al analizar; las Inspection ROIs se mueven/rotan únicamente si no están congeladas.
- Datasets por ROI: cada ROI tiene su dataset OK/NG, su “train memory fit”, “calibrate threshold” y “evaluate”.
- Miniaturas reales: las miniaturas muestran el recorte exacto del ROI (square, circle o annulus).
- Logging reducido: menos ruido por defecto; logs clave en análisis/carga.

## Requisitos rápidos
- Windows 10/11 con .NET 6+ (o la versión específica del proyecto) para la GUI WPF.
- Python 3.11–3.12 para el backend (compatible con torch 2.5.x).
- Modelos/weights gestionados por el backend; ver `Prompt_Backend_PatchCore_DINOv2.md` para configuración avanzada.
- GPU NVIDIA opcional para acelerar inferencia/entrenamiento (ver [Setup](docs/SETUP.md)).

## Uso básico desde la GUI
1. **Cargar imagen** desde el botón principal (arriba-izquierda) y esperar al render del layout.
2. **Cargar preset** desde `presets/` (diálogo de archivo). El preset no incluye las Inspection ROIs 1–4.
3. **Definir Inspection ROIs** (1–4) y pulsar **Guardar ROI** para congelar. Alternar con **Editar ROI** para reactivar adorners.
4. **Analyze Master** recoloca Masters e inspections ligadas (si no están congeladas) según cruces y ángulos.
5. Para cada ROI activa, utilizar los comandos: **Add to OK**, **Add to NG**, **Train memory fit**, **Calibrate threshold**, **Evaluate**.
6. Monitorizar los logs para revisar resultados y errores (nivel reducido pero con eventos clave).

## Documentación ampliada
- [Arquitectura](docs/ARQUITECTURA.md)
- [Backend](docs/BACKEND.md)
- [API (endpoints y JSON)](docs/API.md)
- [Pipeline de detección](docs/PIPELINE_DETECCION.md)
- [Dataset, metadatos y ROIs](docs/DATASET_Y_ROI.md)
- [GUI (WPF)](docs/GUI.md)
- [Setup (Windows/WSL, CUDA/Torch)](docs/SETUP.md)
- [CI/CD](docs/CI_CD.md)
- [Troubleshooting](docs/TROUBLESHOOTING.md)
- [FAQ](docs/FAQ.md)

## Recursos adicionales
- `ARCHITECTURE.md` y `DEV_GUIDE.md` para decisiones históricas de diseño.
- `DATA_FORMATS.md` y `ROI_AND_MATCHING_SPEC.md` para detalles de geometrías, presets y reposicionamiento.
- `DEPLOYMENT.md` para despliegues productivos (contenedores, orquestadores).
- `LOGGING.md` para política de trazas y niveles.
- `instructions_codex_gui_workflow.md` para flujos propuestos al colaborar mediante agentes.

## Comunidad y contribuciones
Las contribuciones se gestionan mediante issues y pull requests. Consulta `CONTRIBUTING.md` antes de proponer cambios y respeta las restricciones descritas en `agents.md` (no modificar adorners ni contratos de backend sin alineación previa).
