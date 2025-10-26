# Dataset, Metadatos y ROIs

## Organización de carpetas
Las muestras se almacenan en `data/` y se dividen por ROI. La estructura recomendada es:
```
data/
  rois/
    Inspection_1/
      ok/    # PNG + JSON
      ng/    # PNG + JSON
    Inspection_2/
      ok/
      ng/
  datasets/
    <roiId>/
      ok/
      ng/
```
- `data/rois/Inspection_<n>` es la ruta principal utilizada cuando la GUI define un `DatasetPath` específico.
- `data/datasets/<roiId>` es un fallback para casos sin configuración explícita.

## Metadatos por muestra
Junto a cada imagen se escribe un archivo `.json` con información mínima:
```json
{
  "role_id": "Inspection",
  "roi_id": "Inspection_1",
  "mm_per_px": 0.023,
  "timestamp_utc": "2025-10-26T12:12:12.345Z",
  "source": "gui-capture"
}
```
Campos adicionales opcionales:
- `operator_id`: identificador del operario que capturó la muestra.
- `preset_name`: preset de ROI activo al momento del guardado.
- `notes`: comentarios libres (defectos específicos, incidencias).

## Gestión de ROIs en la GUI
- **Formas soportadas**: `Rect`, `Circle`, `Annulus`. Cada forma mantiene parámetros canónicos (`X`, `Y`, `Width`, `Height`, `Radius`, etc.) y sincroniza su geometría con los controles de dibujo.
- **Toggle Inspection**: al activarlo, el ROI establece `IsFrozen=true`. En WPF esto implica:
  - `IsHitTestVisible=false` en el shape principal para evitar arrastre.
  - Ocultar adorners o mostrarlos en modo lectura.
  - Mantener la geometría bloqueada hasta que se pulse “Editar ROI”.
- **Sincronización UI**: los cambios en ComboBox o toggles de herramienta actualizan `SelectedInspectionRoi.Shape` y la herramienta activa (`DrawRect`, `DrawCircle`, `DrawAnnulus`).

## Persistencia de presets y masters
- Los presets guardan Masters y ajustes globales pero **no** incluyen las Inspection ROIs 1–4 (se definen después).
- Al cargar un preset, la GUI restaura el layout base y espera a que el usuario dibuje/congele las ROIs de inspección.

## mm/px y escalado
- `mm_per_px` es crucial para convertir distancias y áreas en medidas físicas. Se almacena por muestra para permitir recalibraciones futuras.
- Si la imagen se reescala antes de enviarse al backend, la GUI debe actualizar el `mm_per_px` proporcionalmente.

## Buenas prácticas de curación
- Capturar variedad de muestras OK (≥50) para cubrir la dispersión del proceso.
- Etiquetar NG representativos (defectos reales) y mantener notas en los metadatos.
- Revisar periódicamente la integridad de carpetas (pares PNG/JSON) mediante scripts o validaciones automáticas.
- Versionar datasets críticos (copias en `data/backups/` con fecha) para auditar reentrenos.
