# 📌 Actualización — 2025-10-07

**Cambios clave documentados en esta versión:**
- Definición de ROI basada en `RoiCropUtils` y adorners actuales; se aclara que la GUI exporta PNG + `shape_json` sin alterar geometría.
- Sincronización de máscaras con `backend/roi_mask.py` y conversión px↔mm² usada en `InferenceEngine`.
- Se detalla el flujo de canonicalización (rotación con `Cv2.WarpAffine`, recorte centrado) y la alineación del heatmap en la GUI.

# ROI_AND_MATCHING_SPEC — BrakeDiscInspector

Especificación de las Regiones de Interés (ROI) y de la información geométrica compartida entre la GUI y el backend PatchCore. El pipeline legado de template matching está retirado; la prioridad es mantener los contratos ROI ↔ backend estables.

---

## Índice rápido

- [Modelo de ROI en la GUI](#1-modelo-de-roi-en-la-gui)
- [Canonicalización del ROI](#2-canonicalización-del-roi)
- [Shapes soportados](#3-shapes-soportados)
- [Conversión de coordenadas](#4-conversión-de-coordenadas)
- [Integración con el backend](#5-integración-con-el-backend)
- [Persistencia de datasets](#6-persistencia-de-datasets)
- [Áreas y unidades](#7-áreas-y-unidades)

---

## 1) Modelo de ROI en la GUI

Las clases `ROI.cs` y `RoiShape.cs` describen el modelo utilizado por los adorners (`RoiAdorner`, `ResizeAdorner`, `RoiRotateAdorner`). Cada ROI almacena:

| Campo | Tipo | Descripción |
|-------|------|-------------|
| `Left` / `Top` | double | Coordenadas del bounding box en píxeles de la imagen original. |
| `Width` / `Height` | double | Dimensiones del rectángulo. Para círculos/annulus se normaliza a bbox cuadrado. |
| `AngleDeg` | double | Rotación aplicada desde la GUI (horario). |
| `Shape` | enum | `Rectangle`, `Circle` o `Annulus`. |
| `R`, `RInner` | double | Radios para círculos/annulus. |

Los adorners mantienen la posición y ángulo en sincronía con la imagen mostrada usando `Stretch="Uniform"`, preservando la relación imagen↔canvas.【F:gui/BrakeDiscInspector_GUI_ROI/RoiAdorner.cs†L1-L200】

---

## 2) Canonicalización del ROI

`RoiCropUtils` centraliza el pipeline utilizado para exportar el ROI canónico (idéntico al que se usa en “Save Master/Pattern”):

1. `TryBuildRoiCropInfo` calcula el bounding box exacto y el pivote de rotación en coordenadas de imagen para cualquier shape.【F:gui/BrakeDiscInspector_GUI_ROI/RoiCropUtils.cs†L5-L60】
2. `TryGetRotatedCrop` aplica `Cv2.WarpAffine` sobre la imagen original usando el pivote y el ángulo negativo (para “deshacer” la rotación del adorner), y recorta el rectángulo con las dimensiones originales del ROI.【F:gui/BrakeDiscInspector_GUI_ROI/RoiCropUtils.cs†L62-L134】
3. `BuildRoiMask` genera una máscara 8-bit alineada con el recorte, centrando círculos/annulus en el ROI canónico.【F:gui/BrakeDiscInspector_GUI_ROI/RoiCropUtils.cs†L136-L200】
4. El PNG resultante (BGR) y la máscara opcional se serializan en disco junto con `shape_json`.

Resultado: el backend recibe siempre imágenes alineadas, sin necesidad de re-rotar o recortar.

---

## 3) Shapes soportados

El `shape_json` que acompaña a cada ROI se expresa en coordenadas del ROI canónico. La GUI reutiliza la misma estructura al llamar a `/infer`:

- Rectángulo completo
  ```json
  {"kind":"rect","x":0,"y":0,"w":W,"h":H}
  ```
- Círculo centrado
  ```json
  {"kind":"circle","cx":CX,"cy":CY,"r":R}
  ```
- Annulus
  ```json
  {"kind":"annulus","cx":CX,"cy":CY,"r":R_OUTER,"r_inner":R_INNER}
  ```

`backend/roi_mask.py` reconstruye estas máscaras para enmascarar heatmaps y regiones antes de devolver resultados.【F:backend/roi_mask.py†L1-L160】

---

## 4) Conversión de coordenadas

La imagen principal se muestra con `Stretch="Uniform"`. `MainWindow` y `RoiOverlay` calculan el canvas visible mediante:

```
scale = min(ImageHost.ActualWidth  / PixelWidth,
            ImageHost.ActualHeight / PixelHeight)
drawWidth  = PixelWidth  * scale
drawHeight = PixelHeight * scale
offsetX = (ImageHost.ActualWidth  - drawWidth)  / 2
offsetY = (ImageHost.ActualHeight - drawHeight) / 2
```

- **Imagen → Canvas**: `canvas = image * scale + offset`.
- **Canvas → Imagen**: `image = (canvas - offset) / scale`.

Los adorners operan exclusivamente en coordenadas de imagen, evitando drift al redimensionar la ventana.

---

## 5) Integración con el backend

- `/fit_ok` y `/infer` reciben el PNG canónico; no se envía la imagen original ni se realizan rotaciones en el backend.【F:backend/app.py†L46-L118】
- `/infer` acepta `shape` como string JSON; se aplica en `InferenceEngine.run` para limitar el heatmap antes de calcular percentiles y contornos.【F:backend/infer.py†L66-L132】
- Las `regions` devueltas están en píxeles del ROI canónico; la GUI puede convertirlas a la imagen original si necesita superponer bounding boxes en la vista general.

---

## 6) Persistencia de datasets

`DatasetManager.SaveSampleAsync` crea la estructura `datasets/<role>/<roi>/<ok|ng>/SAMPLE_yyyyMMdd_HHmmssfff.png` y su JSON asociado:
```json
{
  "role_id": "Master1",
  "roi_id": "Pattern",
  "mm_per_px": 0.20,
  "shape_json": "{...}",
  "source_path": "C:/captures/raw.png",
  "angle": 32.0,
  "timestamp": "2025-09-28T12:34:56.789Z"
}
```
Este esquema coincide con `SampleMetadata` y se usa para reconstruir listas de muestras en la GUI.【F:gui/BrakeDiscInspector_GUI_ROI/Workflow/DatasetManager.cs†L38-L74】【F:gui/BrakeDiscInspector_GUI_ROI/Workflow/DatasetSample.cs†L72-L120】

---

## 7) Áreas y unidades

- `mm_per_px` se conserva desde la exportación del dataset hasta las llamadas al backend para asegurar coherencia.
- `InferenceEngine` convierte `area_mm2_thr` a píxeles (`mm2_to_px2`) antes de filtrar contornos y calcula `area_mm2` en la respuesta para cada región.【F:backend/infer.py†L133-L181】
- La GUI debe mostrar ambos valores (px y mm²) y permitir comparar `score` vs `threshold` calibrado.

---

Para más detalles sobre arquitectura y contratos, revisa [ARCHITECTURE.md](ARCHITECTURE.md) y [API_REFERENCE.md](API_REFERENCE.md).
