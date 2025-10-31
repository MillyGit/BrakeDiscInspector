# Arquitectura de BrakeDiscInspector

## Visión global
BrakeDiscInspector combina una interfaz de escritorio WPF con un backend Python especializado en visión por computador. La separación permite evolucionar cada capa de forma independiente y desplegar el backend en estaciones con GPU mientras la GUI se ejecuta en los puestos de inspección. La comunicación se realiza mediante HTTP (REST) y JSON, manteniendo un contrato estable documentado en `agents.md`.

- **GUI (WPF .NET)**: define ROIs canónicas (rectángulo, círculo, anillo), orquesta acciones de dataset (añadir OK/NG, entrenar, calibrar, evaluar), muestra overlays y miniaturas, y expone controles de modo “Master/Search”. Se apoya en MVVM ligero (`WorkflowViewModel`) y comandos asincrónicos que delegan la lógica de negocio al backend.
- **Backend (FastAPI + PyTorch/timm/OpenCV/FAISS)**: expone endpoints `/health`, `/fit_ok`, `/calibrate_ng` y `/infer` para gestionar memorias PatchCore, calibraciones y predicción sobre ROIs canónicas. Persiste artefactos en `models/` y aprovecha GPU cuando `torch.cuda.is_available()`.

## Diagrama de componentes (texto)
```
MainWindow.xaml / MainWindow.xaml.cs
        ↓ (binding, eventos)
Workflow/WorkflowViewModel.cs (comandos, estado)
        ↓ (DataContext)
Controls/WorkflowControl.xaml (UI de dataset/acciones)
        ↓
HttpClient (async) ↔ Backend REST (FastAPI app)
        ↓
backend/app.py → features.py → (timm/torch, faiss)
               → storage.py → persistencia en `models/`
```

## Interacciones clave entre capas
- **Fit OK / Calibrate NG**: la GUI recorta la ROI canónica, empaqueta campos como `role_id`, `roi_id`, `mm_per_px` y envía peticiones `POST /fit_ok` y `POST /calibrate_ng`. El backend extrae embeddings, construye el coreset PatchCore, guarda memorias en `models/` y registra el umbral resultante.
- **Toggle Inspection**: la GUI alterna `IsFrozen` en el modelo de ROI. Cuando está congelado, se deshabilitan adorners (`IsHitTestVisible=false`) y se evita el arrastre hasta que el usuario pulse “Editar ROI”. El backend no participa en esta acción pero depende del ROI canónico resultante.
- **Infer**: la GUI envía `POST /infer` con la ROI actual (y máscara opcional). El backend ejecuta PatchCore + DINOv2, devuelve `score`, `threshold`, `heatmap` y regiones destacadas. La GUI interpreta la respuesta y actualiza indicadores visuales.

## Concerns transversales
- **Sincronización de metadatos**: cada ROI tiene un identificador (`roi_id`), resolución (mm/px) y estado de congelación que deben mantenerse consistentes entre GUI y backend.
- **Gestión de errores**: la GUI ejecuta las llamadas HTTP en tareas asíncronas, captura excepciones y muestra mensajes sin bloquear el hilo principal. El backend valida campos obligatorios y devuelve errores JSON estructurados.
- **Escalabilidad**: el backend puede desplegarse como servicio independiente (Docker/WSL). La GUI puede apuntar a distintos hosts configurando la URL base.

## Relación con documentación complementaria
- [docs/BACKEND.md](BACKEND.md) detalla módulos Python, rutas y esquemas.
- [docs/GUI.md](GUI.md) describe componentes WPF, comandos y UX.
- [docs/PIPELINE_DETECCION.md](PIPELINE_DETECCION.md) explica las etapas de PatchCore/DINOv2 y estrategias de rendimiento.
- [docs/DATASET_Y_ROI.md](DATASET_Y_ROI.md) resume la persistencia de imágenes y metadatos en disco.
