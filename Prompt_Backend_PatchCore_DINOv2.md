# Prompt para Codex — Backend de Detección de Anomalías (PatchCore + DINOv2) — **Sin EfficientNet**

## 🔧 CONTEXTO DEL PROYECTO (NO CAMBIAR LA GUI)

- La **GUI WPF** ya permite: dibujar ROI (rect/círculo/annulus), rotar, guardar/recargar, y mostrar overlays (heatmaps/contornos).  
- Tenemos utilidades fiables de **recorte + rotación canónica** del ROI (p. ej., `RoiCropUtils` en C#) y ya generamos **ROI canónicos** (imágenes alineadas).  
- **No modifiques la GUI** ni los Adorners: solo **backend**.  
- El backend debe exponer **endpoints HTTP** limpios para:  
  - `POST /fit_ok` (acumular OK para “aprender lo normal”)  
  - `POST /calibrate_ng` (0–3 NG para umbral por ROI/rol)  
  - `POST /infer` (devolver score, heatmap y contornos)  
- **Descarta EfficientNet** para este trabajo. Usa **DINOv2 ViT-S** como extractor de características *congelado*.  
- Piezas: **metálicas o pintadas en gris** (discos de freno, manguetas). Defectos **de milímetros**. Nosotros ya aportamos **ROI canónico** (rotado/recortado) antes de enviar al backend.  
- Necesitamos detección por **anomalía “good-only”** estilo **PatchCore** con **coreset** y kNN (FAISS/sklearn), **sin entrenar** con NG, y usar **0–3 NG** solo para **calibrar** el umbral.


## 🎯 OBJETIVO TÉCNICO

Implementar un **microservicio backend** (Python) con FastAPI (o Flask), que:

1) **Recibe ROI canónico** (p. ej., 384×384) + metadatos del ROI (id, rol, mm/px y shape opcional para enmascarado).  
2) **Extractor**: DINOv2 ViT-S **congelado** (pretrained) → **features por parches** multi-escala.  
3) **Memoria OK (PatchCore)**:
   - Construye embeddings por parche.  
   - Aplica **coreset** (k-center greedy) para reducir a **1–5%** (configurable).  
   - Crea índice **kNN** con FAISS (o `sklearn.NearestNeighbors` si no hay FAISS).  
4) **Inferencia**:
   - kNN por parche → **mapa de distancias** (heatmap).  
   - **Score global** = p99 (o p95) del mapa (configurable).  
5) **Calibración** (roles/ROIs):  
   - Con **0–3 NG**: fija umbral por ROI entre **p99 de OK** y **p5 de NG** (si hay NG).  
   - Persistir **threshold** por `role_id`/`roi_id`.  
6) **Posproceso**:
   - **Suavizado** ligero (Gaussian blur).  
   - **Máscara** del ROI (rect/círculo/annulus) para limitar el heatmap (si se proporciona shape).  
   - **Eliminar islas** con área inferior a **X mm²** (convertir a px con `px_area_thr = area_mm2 / (mm_per_px^2)`).  
   - **Contornos** y **bounding boxes** (OpenCV).  
7) **Salida** para la GUI:
   - **Score** (float), **threshold** aplicado,  
   - **Heatmap** (PNG base64 o ruta temporal),  
   - **Contornos**: lista con bbox en píxeles, área en px y **área en mm²** (usar `mm_per_px`).  

**Requisitos hard**:  
- **No usar EfficientNet**.  
- **No toca GUI**.  
- **Determinismo** opcional (seed).  
- **Device**: usar GPU si existe, si no CPU.  
- **Artefactos persistentes** por `role_id`/`roi_id` (memoria, índice, calibración).


## 📦 ESTRUCTURA PROPUESTA (código y módulos)

```
backend/
  app.py                # FastAPI con endpoints /fit_ok, /calibrate_ng, /infer, /health
  features.py           # Wrapper DINOv2 ViT-S (timm), preprocesado, extracción multi-escala
  patchcore.py          # Memoria OK, coreset (k-center greedy), kNN (FAISS/sklearn)
  calib.py              # Calibración/thresholds por ROI/rol con 0–3 NG
  infer.py              # Pipeline inferencia: embeddings -> knn -> heatmap -> posproceso
  roi_mask.py           # Máscara (rect/circle/annulus) en tamaño ROI (384x384)
  storage.py            # Persistencia artefactos (npz/json) por role_id/roi_id
  utils.py              # helpers: mm/px, seed, logging, timers
  requirements.txt      # torch, timm (dinov2), faiss-cpu, fastapi, uvicorn, numpy, opencv-python, scikit-learn, scipy
  README_backend.md     # cómo levantar el servicio y formatos I/O
  models/
    <role_id>/<roi_id>/memory.npz        # embeddings coreset, token_shape, etc.
    <role_id>/<roi_id>/index.faiss       # índice FAISS (si aplica)
    <role_id>/<roi_id>/calib.json        # threshold, p99_ok, p5_ng, mm_per_px, params
```


## 🧠 EXTRACTOR (DINOv2 ViT-S) — **sin entrenamiento**

- Usa `timm` con **dinov2 pequeño**:  
  `timm.create_model('vit_small_patch14_dinov2.lvd142m', pretrained=True)`  
- **Congelado** (`eval()`, `requires_grad=False`).  
- Preprocesado: normalización ImageNet (o la que requiera el peso).  
- **Entrada** ROI canónico **384×384** (configurable).
- **Multi-escala** simple:  
  - o **multi-nivel** (obtener token embeddings de 2–3 capas internas),  
  - o **multi-resize** (p. ej., 256 y 384) y concatenar.  
- Resultado: **mapa de tokens** (H_token × W_token × D). Para patch=14, 384→**27×27** tokens.  
- **L2 normalize** embeddings por parche.

> Implementa `FeaturesExtractor` con:  
> `forward(np.uint8 HxWx3) -> (embeddings: np.ndarray [N_tokens, D], token_hw: (H,W))`.


## 🧩 MEMORIA OK (PatchCore) + CORESEt + kNN

- `fit_ok(images_ok: List[np.uint8])`:
  - Extrae embeddings de cada OK (concatenar por ROI).  
  - **Coreset k-center greedy** para reducir a **1–5%** (`target_rate` configurable).  
  - Construye índice **FAISS IndexFlatL2** (o `NearestNeighbors` sklearn si no hay FAISS).  
  - Persistir: `memory.npz` (embeddings coreset, token_hw, D), `index.faiss`.  

**Coreset k-center greedy** (pseudocódigo):
```
Input: E = [e1..eM] (L2-normalized), size M × D, target m = ceil(M * target_rate)
Pick first center c0 (aleatorio o más representativo)
While |C| < m:
  d_i = min_{c in C} ||e_i - c||_2
  pick argmax_i d_i  -> nuevo centro
```


## 🔎 INFERENCIA

- `infer(image_roi: np.uint8, role_id, roi_id, mm_per_px, [shape])`:
  1. Extrae embeddings → `E_q` (Nq × D).  
  2. Para cada parche, distancia `d_i` al **vecino más cercano** en la memoria.  
  3. **Heatmap**: `H_token×W_token` con `d_i`, reescala a **384×384** (`cv2.resize` bilineal).  
  4. **Score global**: percentil **p99** (o **p95**) del heatmap (config).  
  5. **Posproceso**:
     - **Gaussian blur** (σ pequeño).  
     - **Máscara ROI** (rect/círculo/annulus) → pone a 0 fuera del ROI.  
     - **Umbral**: si calibrado, usar `threshold` (sino modo “score-only”).  
     - **Islas**: elimina contornos con área < `px_area_thr = area_mm2_thr/(mm_per_px^2)`.  
     - **Contornos**: `cv2.findContours`; exporta `bbox`, `area_px`, `area_mm2`.  
  6. Devuelve:
     ```json
     {
       "role_id": "...",
       "roi_id": "...",
       "score": 0.0,
       "threshold": 0.0,
       "heatmap_png_base64": "...",
       "regions": [
         {"bbox": [x,y,w,h], "area_px": n, "area_mm2": a, "contour": [[x1,y1], ...]}
       ],
       "token_shape": [H_token, W_token],
       "params": { "extractor": "dinov2-vit-s/14", "coreset_rate": 0.02, "k": 1, "p_score": 99, "blur_sigma": 1.0 }
     }
     ```
- **Tiempo**: vectoriza distancias; en FAISS, consulta en batch.


## ⚖️ CALIBRACIÓN (con 0–3 NG)

- `calibrate_ng(role_id, roi_id, samples_ok_scores, samples_ng_scores)`:
  - Si **hay NG**: `threshold` entre **p99(OK)** y **p5(NG)**; prueba grid corto (o mediana) para minimizar FP/FN.  
  - Si **no hay NG**: `threshold = p99(OK)` (+ margen configurable).  
  - Guarda `calib.json`: `{"threshold": t, "p99_ok": x, "p5_ng": y|null, "mm_per_px": z, "area_mm2_thr": A, "score_percentile": 99}`.

> `area_mm2_thr` configurable por ROI/rol (p. ej. 1.0–2.0 mm²). Convierte a px dentro de `infer`.


## 🛠️ ENDPOINTS HTTP (FastAPI)

### `POST /fit_ok`
- **multipart/form-data**:
  - `role_id`: str  
  - `roi_id`: str  
  - `mm_per_px`: float  
  - `images`: uno o varios PNG/JPG (ROI canónico; si no es 384, redimensionar internamente)
- **acción**: extraer features, acumular, coreset, construir índice, persistir.
- **respuesta**: JSON con conteos (`n_ok_total`, `n_embeddings`, `coreset_rate_applied`) y checksum.

### `POST /calibrate_ng`
- **JSON**:
  ```json
  {
    "role_id": "...",
    "roi_id": "...",
    "mm_per_px": 0.2,
    "ok_scores": [ ... ],
    "ng_scores": [ ... ],
    "area_mm2_thr": 1.0,
    "score_percentile": 99
  }
  ```
- **acción**: fija y guarda `threshold` por ROI/rol.
- **respuesta**: JSON con `threshold`, `p99_ok`, `p5_ng` (si aplica).

### `POST /infer`
- **multipart/form-data**:
  - `role_id`, `roi_id`, `mm_per_px`  
  - `shape` (opcional): `{ "kind": "circle"|"annulus"|"rect", "cx":..., "cy":..., "r":..., "r_inner":..., "w":..., "h":... }`  
  - `image`: PNG/JPG (ROI canónico 384×384 ideal; si no, resize interno).
- **acción**: embeddings → kNN → heatmap → posproceso → score + contornos.
- **respuesta**: JSON como arriba.

### `GET /health`
- `{ "status": "ok", "device": "cuda"|"cpu", "model": "dinov2-vit-s/14", "version": "..." }`.


## ✅ CRITERIOS DE ACEPTACIÓN

- **Sin EfficientNet** (ni código ni deps).  
- Arranca con `uvicorn app:app`.  
- `/fit_ok`, `/calibrate_ng`, `/infer` funcionales con artefactos persistentes por `(role_id, roi_id)`.  
- **Determinismo** opcional (seed).  
- **CUDA si existe**, si no CPU.  
- **Tests mínimos**:
  - Fit con 20–50 OK → coreset 1–5%.  
  - Infer con 1 OK y 1 “simulada NG” → heatmap razonable.  
  - Calibración con 0 NG (p99 OK) y con 1–3 NG (umbral entre p99 OK y p5 NG).  
  - Eliminación de islas < área_mm2_thr (mm ↔ px correcto).  
- Rendimiento orientativo (GPU): `<60 ms` por ROI 384×384 (extract + kNN coreset ~10k). CPU aceptable con batch pequeño.


## 📝 DETALLES DE IMPLEMENTACIÓN

- **features.py**
  - `class DinoV2Features:`  
    - `__init__(model_name='vit_small_patch14_dinov2.lvd142m', device='auto', out_indices=(9, 11))`  
    - `extract(img_bgr_uint8) -> (embeddings[N,D], (Ht,Wt))`  
      - Preprocesa a RGB, resize a 384, normaliza.  
      - Extrae tokens de capas `out_indices`; concatena y **L2-normalize**.  
- **patchcore.py**
  - `build_memory(emb_list: List[np.ndarray], coreset_rate=0.02, seed=0)` → `memory: np.ndarray[M,D]`  
  - `build_index(memory)` → `faiss.IndexFlatL2` (o sklearn)  
  - `knn(query: np.ndarray[N,D], k=1)` → `dist_min[N]`  
  - Persistencia con `np.savez` y FAISS `write_index`.
- **infer.py**
  - `infer_one(image, role_id, roi_id, mm_per_px, shape=None, k=1, p_score=99, blur_sigma=1.0, area_mm2_thr=1.0)`  
    - Usa `storage.load(role, roi)` para memoria/índice/threshold.  
    - Heatmap token → resize → blur → mask → binariza con `threshold` (si calibrado) → contornos.  
    - `area_mm2 = area_px * (mm_per_px**2)`.  
- **roi_mask.py**
  - Genera máscara binaria 384×384 desde `shape` (rect/circle/annulus).  
- **calib.py**
  - `calibrate(ok_scores, ng_scores=None, percentile=99)` → `threshold`.
- **storage.py**
  - Rutas `models/<role>/<roi>/...`, manejo JSON.


## 📡 EJEMPLO DE LLAMADA DESDE C# (GUI ACTUAL)

```csharp
// infer
var content = new MultipartFormDataContent();
content.Add(new StringContent(roleId), "role_id");
content.Add(new StringContent(roiId), "roi_id");
content.Add(new StringContent(mmPerPx.ToString(CultureInfo.InvariantCulture)), "mm_per_px");
content.Add(new StreamContent(File.OpenRead(roiPngPath)), "image", "roi.png");
// opcional: content.Add(new StringContent(JsonConvert.SerializeObject(shape)), "shape");

var resp = await http.PostAsync($"{baseUrl}/infer", content);
var json = await resp.Content.ReadAsStringAsync();
// parsea score, threshold, heatmap (base64), regions[bbox...]
```


## ❓ PREGUNTAS QUE PUEDE HACER EL MODELO

1) ¿Confirmas **entrada** del backend será **ROI canónico** (rotado/recortado)? (sí)  
2) ¿Formato de `shape` para máscara (circle/annulus/rect) lo envía la GUI? (opcional; si falta, aplicar máscara “lleno”)  
3) ¿Tamaño fijo 384×384 o configurable? (por defecto 384; param `input_size`)  
4) ¿FAISS disponible en despliegue o usar sklearn si no? (soporta ambos)  
5) ¿Dónde guardar artefactos? (ruta `models/`)  
6) `area_mm2_thr` por ROI/rol, ¿valor inicial? (p. ej. **1.0 mm²**)  
7) ¿Devolver heatmap **base64** o **ruta temporal**? (base64 por defecto)  


## ⛔ EXCLUSIONES

- **No** usar EfficientNet ni cargar sus pesos.  
- **No** tocar GUI ni lógica WPF.  
- **No** suponer que hay más de 0–3 NG reales para entrenar.  


## ✅ ENTREGA ESPERADA

- Código completo bajo `backend/` con los módulos descritos, `requirements.txt`, `README_backend.md`.  
- Servidor FastAPI ejecutable (`uvicorn app:app`) y endpoints funcionando.  
- Scripts/funciones unitarias mínimas para probar `fit_ok`, `calibrate_ng`, `infer`.
