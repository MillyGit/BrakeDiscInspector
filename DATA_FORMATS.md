Estructura de datos y rutas
Presets

Carpeta fija fuera de datasets: presets/ (ruta configurable). Un preset no incluye Inspections 1–4.

Formato (JSON):

{
  "version": 2,
  "master1": { /* ROI + meta */ },
  "master1Inspection": { /* ROI + meta */ },
  "master2": { /* ROI + meta */ },
  "master2Inspection": { /* ROI + meta */ },
  "scale_lock": true,
  "use_local_matcher": true,
  "mm_per_px": 0.1
}

Datasets por ROI

Base raíz única de imágenes de entrada: images/
Carpeta datasets separada por cada Inspection ROI:

datasets/
  inspection-1/
    ok/
      0001.png
      0002.png
    ng/
      0003.png
      0004.png
  inspection-2/
    ok/...
    ng/...
snapshots/         # recortes de ROI hechos desde `images/`
  inspection-1/
    ok/
    ng/
presets/           # presets fuera del dataset


Miniaturas: se generan a partir de snapshots/inspection-X/{ok|ng}/*.png aplicando la máscara real (square/circle/annulus).
