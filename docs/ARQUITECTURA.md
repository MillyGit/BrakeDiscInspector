# Arquitectura de BrakeDiscInspector

## Visión global
BrakeDiscInspector combina una interfaz de escritorio WPF con un backend Python especializado en visión por computador. La separación permite evolucionar cada capa de forma independiente y desplegar el backend en estaciones con GPU mientras la GUI se ejecuta en los puestos de inspección. La comunicación se realiza mediante HTTP (REST) y JSON, manteniendo un contrato estable documentado en `agents.md`.

- **GUI (WPF .NET)**: define ROIs canónicas (rectángulo, círculo, anillo), orquesta acciones de dataset (añadir OK/NG, entrenar, calibrar, evaluar), muestra overlays y miniaturas, y expone controles de modo “Master/Search”. Se apoya en MVVM ligero (`WorkflowViewModel`) y comandos asincrónicos que delegan la lógica de negocio al backend.
- **Backend (FastAPI + PyTorch/timm/OpenCV/Faiss)**: ofrece una API REST para extracción de características, gestión de coresets/datasets, entrenamiento y predicción. Implementa lógica de persistencia de muestras en disco y aprovecha la GPU cuando `torch.cuda.is_available()`.

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
               → dataset_manager.py → almacenamiento en disco
```

## Interacciones clave entre capas
- **Add to OK / Add to NG**: la GUI recorta la ROI canónica, empaqueta campos como `role_id`, `roi_id`, `mm_per_px` y envía una petición `POST /fit_ok` o `POST /send_ng`. El backend persiste la imagen (`PNG`) y el JSON de metadatos en `data/rois/...`, actualiza el coreset en memoria (si `memory_fit=true`) y responde con las rutas creadas. La GUI refresca la lista de miniaturas.
- **Toggle Inspection**: la GUI alterna `IsFrozen` en el modelo de ROI. Cuando está congelado, se deshabilitan adorners (`IsHitTestVisible=false`) y se evita el arrastre hasta que el usuario pulse “Editar ROI”. El backend no participa en esta acción pero depende del ROI canónico resultante.
- **Predict**: la GUI envía `POST /predict` con la ROI actual. El backend ejecuta inferencia (PatchCore + DINOv2), genera el score, evalúa contra umbrales y opcionalmente produce heatmaps. La GUI interpreta la respuesta y actualiza indicadores visuales.
- **Train status**: tanto la GUI como los tests (`tests/test_app_train_status.py`) consultan `GET /train/status` para mostrar colas de entrenamiento, dispositivo activo y la marca temporal del último fit.

## Concerns transversales
- **Sincronización de metadatos**: cada ROI tiene un identificador (`roi_id`), resolución (mm/px) y estado de congelación que deben mantenerse consistentes entre GUI y backend.
- **Gestión de errores**: la GUI ejecuta las llamadas HTTP en tareas asíncronas, captura excepciones y muestra mensajes sin bloquear el hilo principal. El backend valida campos obligatorios y devuelve errores JSON estructurados.
- **Escalabilidad**: el backend puede desplegarse como servicio independiente (Docker/WSL). La GUI puede apuntar a distintos hosts configurando la URL base.

## Relación con documentación complementaria
- [docs/BACKEND.md](BACKEND.md) detalla módulos Python, rutas y esquemas.
- [docs/GUI.md](GUI.md) describe componentes WPF, comandos y UX.
- [docs/PIPELINE_DETECCION.md](PIPELINE_DETECCION.md) explica las etapas de PatchCore/DINOv2 y estrategias de rendimiento.
- [docs/DATASET_Y_ROI.md](DATASET_Y_ROI.md) resume la persistencia de imágenes y metadatos en disco.
