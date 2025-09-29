# ROI_AND_MATCHING_SPEC — BrakeDiscInspector

Especificación de las Regiones de Interés (ROI) y de la información geométrica que intercambian la GUI y el backend PatchCore. La funcionalidad de *template matching* legado ha sido retirada; este documento se centra en el flujo ROI → backend.

---

## 1) Modelo de ROI en la GUI

| Campo | Tipo | Descripción |
|-------|------|-------------|
| `X` | double | Coordenada X del centro (en píxeles de la imagen original). |
| `Y` | double | Coordenada Y del centro. |
| `Width` | double | Ancho del rectángulo que delimita el ROI (mínimo 10 px). |
| `Height` | double | Alto del rectángulo. |
| `AngleDeg` | double | Ángulo de rotación (grados) aplicado alrededor del centro. |
| `Legend` | string | Etiqueta descriptiva (ej. `"Pattern"`, `"Inspection"`). |

Restricciones:
- El ROI debe permanecer dentro de los límites de la imagen; si se sale parcialmente, la GUI recorta la zona válida.
- `AngleDeg` solo se utiliza en la GUI. El backend recibe un PNG ya rotado (ROI canónico).

---

## 2) Canonicalización del ROI

1. La GUI rota la imagen completa usando `Cv2.GetRotationMatrix2D` y `Cv2.WarpAffine` alrededor del centro del ROI.
2. Posteriormente recorta el rectángulo `Width × Height` sobre la imagen rotada.
3. El resultado es un PNG (ROI canónico) que se envía al backend en `/fit_ok` y `/infer`.
4. La GUI genera un archivo JSON asociado con metadatos:
   ```json
   {
     "role_id": "Master1",
     "roi_id": "Pattern",
     "mm_per_px": 0.20,
     "shape": { "kind": "circle", "cx": 192, "cy": 192, "r": 180 },
     "source_path": "C:/datasets/raw.png",
     "angle": 32.0,
     "timestamp": "2024-06-01T12:34:56.789Z"
   }
   ```

---

## 3) Shapes soportados por el backend

El campo `shape` es opcional y se expresa siempre en coordenadas del ROI canónico (post rotación/crop).

- **Rectángulo completo**:
  ```json
  {"kind":"rect","x":0,"y":0,"w":W,"h":H}
  ```
- **Círculo**:
  ```json
  {"kind":"circle","cx":CX,"cy":CY,"r":R}
  ```
- **Annulus**:
  ```json
  {"kind":"annulus","cx":CX,"cy":CY,"r":R_OUTER,"r_inner":R_INNER}
  ```

Si el shape no se envía, el backend asume todo el ROI.

---

## 4) Conversión de coordenadas GUI ↔ imagen

La imagen se muestra con `Stretch="Uniform"`. Para alinear el canvas de ROI:

```
scale = min(ImageHost.ActualWidth  / PixelWidth,
            ImageHost.ActualHeight / PixelHeight)
drawWidth  = PixelWidth  * scale
drawHeight = PixelHeight * scale
offsetX = (ImageHost.ActualWidth  - drawWidth)  / 2
offsetY = (ImageHost.ActualHeight - drawHeight) / 2

CanvasROI.Width  = drawWidth
CanvasROI.Height = drawHeight
Canvas.SetLeft(CanvasROI, offsetX)
Canvas.SetTop(CanvasROI,  offsetY)
```

Conversión:
- **Imagen → Canvas**: `(canvasX, canvasY) = (imageX * sx, imageY * sy)`.
- **Canvas → Imagen**: `(imageX, imageY) = (canvasX / sx, canvasY / sy)`.

---

## 5) Integración con el backend

- `/fit_ok` y `/infer` reciben siempre el PNG canónico (no raw image) y, opcionalmente, el `shape` JSON serializado como string.
- `/calibrate_ng` consume únicamente los scores devueltos por `/infer`; no recibe geometría.
- El backend devuelve `regions` en píxeles del ROI canónico; la GUI puede convertirlos a mm² usando `mm_per_px`.

---

## 6) Persistencia de datasets

La GUI guarda muestras en `datasets/<role>/<roi>/<ok|ng>/` con pares PNG/JSON. Se recomienda mantener consistencia en la nomenclatura:
```
datasets/Master1/Pattern/ok/OK_20240601_123456.png
datasets/Master1/Pattern/ok/OK_20240601_123456.json
```

Los archivos JSON deben incluir `role_id`, `roi_id`, `mm_per_px`, `shape`, `angle`, `timestamp` y `source_path` (opcional).

---

## 7) Áreas y unidades

- El backend aplica un filtro de islas usando `area_mm2_thr` (mm²). La GUI debe proporcionar `mm_per_px` correcto para evitar falsos positivos/negativos.
- Las áreas devueltas en `regions` contienen tanto `area_px` como `area_mm2` ya convertida.

---

## 8) Referencias cruzadas

- [ARCHITECTURE.md](ARCHITECTURE.md) — flujo completo GUI ↔ backend.
- [API_REFERENCE.md](API_REFERENCE.md) — contratos de `/fit_ok`, `/calibrate_ng`, `/infer`.
- [DATA_FORMATS.md](DATA_FORMATS.md) — esquemas de archivos y JSON.
