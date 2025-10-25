Especificación de ROIs y Matching
Geometrías

square: center(x,y), size(w,h), rotation_deg.

circle: center(x,y), radius, rotation_deg (opcional para dibujo).

annulus: center(x,y), radius, inner_radius, rotation_deg.

Centro shape-aware

El “centro” para alinear con cruces es el centro geométrico de la forma. Para annulus, el centro del anillo.

Reposicionado (Analyze Master)

Detectar cruces/ángulo para Master 1 y Master 2 (matcher local si está activo).

Calcular Δx, Δy, Δθ globales.

Aplicar transform a:

Master1, Master2, Master1Inspection, Master2Inspection siempre.

Inspection 1–4 solo si IsFrozen == false.

Respetar scale_lock por defecto, con opción para desactivar.

Congelación (Freeze)

“Guardar ROI” → IsFrozen=true y desaparecen adorners.

“Editar ROI” → IsFrozen=false y aparecen adorners.
