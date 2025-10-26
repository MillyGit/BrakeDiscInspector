# GUI WPF

## Componentes principales
- `MainWindow.xaml` / `MainWindow.xaml.cs`: ventana principal que aloja el lienzo de imagen, overlays de ROI y toolbars. Gestiona eventos de carga de imagen, zoom/pan y delega la lógica a su ViewModel.
- `Workflow/WorkflowViewModel.cs`: ViewModel central. Expone comandos (`AddToOkCommand`, `AddToNgCommand`, `TrainMemoryFitCommand`, `PredictCommand`, `OpenDatasetFolderCommand`, etc.), colecciones observables para miniaturas y propiedades de estado (ROI seleccionada, URL del backend, logs).
- `Controls/WorkflowControl.xaml`: panel lateral con campos de dataset, botones de acciones y toggles (`Toggle Inspection`, `DrawRect`, `DrawCircle`, `DrawAnnulus`). Utiliza bindings para reflejar el estado del ViewModel.
- Otros componentes relevantes: `MasterLayout`, `RoiModel`, `RoiShape` y utilidades de exportación canónica (`TryBuildRoiCropInfo`, `TryGetRotatedCrop`). Estos no deben modificarse sin coordinación (ver `agents.md`).

## Flujo de interacción
1. **Carga de imagen**: el usuario selecciona la imagen base. La GUI actualiza el layout y recalcula escalas.
2. **Selección de herramienta**: mediante toggle buttons en la toolbar de dibujo se elige rectángulo, círculo o anillo.
3. **Dibujo de ROI**: el usuario traza la ROI sobre la imagen. El sistema crea el shape correspondiente y lo vincula con `WorkflowViewModel`.
4. **Congelar/editar**: al pulsar “Toggle Inspection”, el ROI se congela (`IsFrozen=true`). Para editarlo de nuevo se pulsa “Editar ROI” que reactiva adorners y habilita arrastre.
5. **Captura de muestra**: con la ROI congelada, los botones `Add to OK` o `Add to NG` generan el recorte canónico, lo envían al backend y almacenan el resultado en disco.
6. **Entrenamiento y evaluación**: `Train memory fit` instruye al backend para actualizar su coreset con las muestras OK. `Evaluate` (o `Predict`) envía la ROI actual y muestra el score devuelto.
7. **Calibración**: si se dispone de NG, se puede ejecutar la calibración para fijar umbrales (dependiendo de la versión del backend). El resultado se muestra en la interfaz.

## Puntos de UX resueltos
- **Toolbars sin solape**: se utiliza `ToolBarTray` con bandas separadas para controles generales y herramientas de dibujo, evitando superposición con el área de imagen.
- **Toggle Inspection visible**: botón con texto en blanco, contraste alto y estado enlazado a `IsFrozen`. Cuando el ROI está congelado, el shape bloquea interacción (`IsHitTestVisible=false`).
- **Open Folder**: `OpenDatasetFolderCommand` abre el explorador en la carpeta del dataset activo (`Process.Start` con ruta). Permite verificar rápidamente los archivos generados.
- **Miniaturas reales**: las colecciones observables mantienen rutas a los recortes generados; se crean thumbnails con el recorte exacto (incluyendo forma circular/annular mediante máscaras).

## Integración con backend
- El ViewModel utiliza `HttpClient` con métodos `async/await` para invocar `/fit_ok`, `/send_ng`, `/predict` y `/train/status`.
- Los errores HTTP se capturan y se registran en el panel de logs, mostrando mensajes amigables.
- La URL base del backend se puede configurar (campo en la UI o archivo de configuración).

## Logging y diagnósticos
- `AppendLog(...)` centraliza la escritura de eventos (peticiones, respuestas, errores).
- Se recomienda habilitar niveles de log diferenciados (info, warning, error) para depurar.
- El panel de logs se puede limpiar manualmente y guarda el historial en memoria mientras la sesión está activa.

## Buenas prácticas de desarrollo
- Mantener MVVM: evitar lógica pesada en code-behind (`MainWindow.xaml.cs`).
- Reutilizar utilidades existentes para exportar ROIs; no reinventar transformaciones.
- Ejecutar comandos de backend fuera del hilo UI (`Task.Run` o métodos `async`).
- Añadir tests automatizados (si procede) para ViewModels puros.
