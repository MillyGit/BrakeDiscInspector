Guía de desarrollo
Estilo y patrones

MVVM ligero. El ViewModel es la fuente de verdad de flags (mostrar overlays, forma, ROI seleccionado).

Evitar acceso directo a controles XAML desde code-behind (no ChkShow*, no Combo*). Usar bindings.

Añadir un nuevo Inspection ROI

Crear RoiModel con Id y Name (Inspection 1..4).

Añadir dataset folders datasets/inspection-X/{ok,ng} y snapshots/inspection-X/{ok,ng}.

Enlazar botones de Add to OK/NG, Train fit, Calibrate, Evaluate a ese ROI.

Miniaturas: generar tras guardar snapshot.

Thresholds

threshold por defecto 0.5 (global). Cada modelo puede mantener calibración propia tras Calibrate threshold (persistencia backend).
