
# ROI_AND_MATCHING_SPEC — BrakeDiscInspector

Este documento define el modelo de **Regiones de Interés (ROI)** y el mecanismo de **matching** usado en la aplicación.

---

## 1) Definición de ROI

Cada ROI representa un área rectangular dentro de la imagen del disco de freno, con rotación y metadatos.

### 1.1 Campos del ROI

| Campo     | Tipo    | Descripción |
|-----------|---------|-------------|
| `X`       | double  | Coordenada X (en píxeles de la imagen) del centro del ROI. |
| `Y`       | double  | Coordenada Y (en píxeles de la imagen) del centro del ROI. |
| `Width`   | double  | Ancho en píxeles. Debe ser ≥ 10. |
| `Height`  | double  | Alto en píxeles. Debe ser ≥ 10. |
| `AngleDeg`| double  | Rotación en grados, aplicada alrededor del centro del ROI. |
| `Legend`  | string  | Etiqueta textual asociada (ej. `"M1"`). |

### 1.2 Restricciones

- ROI mínimo: **10×10 píxeles**.  
- `AngleDeg` se maneja en la GUI (rotación visual), el backend recibe un PNG ya rotado → el backend **no necesita aplicar rotación**.  
- Si el ROI sale parcialmente fuera de la imagen, se recorta al borde válido.

---

## 2) Rotación del ROI

- Implementada en GUI con el thumb NE del `RoiAdorner`.
- Se aplica `Cv2.GetRotationMatrix2D` + `Cv2.WarpAffine` a la imagen completa.  
- Después se extrae el sub-rectángulo correspondiente.  
- Esto garantiza que el backend recibe un crop ya orientado correctamente.

---

## 3) Annulus (anillo de centrado)

El **annulus** es un mecanismo opcional para delimitar un área circular de interés.

### 3.1 Definición JSON

```json
{
  "cx": 400,
  "cy": 300,
  "ri": 50,
  "ro": 200
}
```

- `cx, cy`: centro en píxeles.  
- `ri`: radio interno.  
- `ro`: radio externo.  

### 3.2 Comportamiento

- Los píxeles fuera del anillo se anulan (puestos a negro).  
- Puede combinarse con un ROI: primero se aplica rotación/recorte, luego el annulus sobre el crop.

---

## 4) Matching maestro

Además del análisis de defectos, el backend expone `/match_master` (alias `/match_one`) para localizar un patrón maestro en una captura.

### 4.1 Parámetros soportados

- `image` (**obligatorio**): imagen completa donde buscar.
- `template` (**obligatorio**): plantilla; si es PNG con canal alfa, éste se usa como máscara.
- `feature`: `auto` (default), `sift`, `orb`, `tm_rot` o `geom`.
- `thr`, `tm_thr`: umbrales de coincidencia.
- `rot_range`, `scale_min`, `scale_max`: rango y escalas para el modo `tm_rot`.
- `search_x`, `search_y`, `search_w`, `search_h`: restringen la búsqueda a un ROI previo.
- `debug`: `1/true` para recibir capturas `debug.*` codificadas en Base64.

### 4.2 Respuesta

La respuesta incluye siempre `found`/`stage` y, cuando hay éxito, coordenadas absolutas (`center_x`, `center_y`) y métricas (`confidence`, `tm_best`, `tm_thr`, `sift_orb`, etc.). Ejemplo:

```json
{
  "found": true,
  "stage": "TM_ROT_OK",
  "center_x": 410.2,
  "center_y": 302.7,
  "confidence": 0.93,
  "tm_best": 0.93,
  "tm_thr": 0.8,
  "tm_rot": {
    "angle_deg": -2.0,
    "scale": 1.01
  }
}
```

Si no se alcanza el umbral configurado se devuelve `found=false` junto con `reason`, `tm_best`, `crop_off` y el bloque `sift_orb` indicando el detector usado o el fallo.

---

## 5) Mapeo imagen ↔ canvas

La GUI renderiza la imagen con `Stretch="Uniform"`. Para mantener alineación entre ROI (en canvas) y píxeles reales:

```csharp
scale = Math.Min(ImgMain.ActualWidth / bmp.PixelWidth,
                 ImgMain.ActualHeight / bmp.PixelHeight);
drawWidth  = bmp.PixelWidth  * scale;
drawHeight = bmp.PixelHeight * scale;
offsetX = (ImgMain.ActualWidth  - drawWidth)  / 2;
offsetY = (ImgMain.ActualHeight - drawHeight) / 2;
```

Con este cálculo:  
- `CanvasROI` se coloca en `(offsetX, offsetY)`  
- `CanvasROI.Width = drawWidth`  
- `CanvasROI.Height = drawHeight`  

Conversión coordenadas:  
- Imagen → Canvas: `(imageX * sx, imageY * sy)`  
- Canvas → Imagen: `(canvasX / sx, canvasY / sy)`

---

## 6) Decisión de clasificación

El backend determina la etiqueta final con la regla:

```
if score >= threshold → label = "NG"
else → label = "OK"
```

- `score`: salida del modelo ∈ [0,1]  
- `threshold`: valor leido de `model/threshold.txt`

---

## 7) Referencias cruzadas

- **ARCHITECTURE.md**: descripción de flujo GUI↔backend  
- **API_REFERENCE.md**: contratos de endpoints  
- **DATA_FORMATS.md**: formatos de requests/responses  
