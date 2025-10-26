# Backend Anomalías — PatchCore + DINOv2 (sin EfficientNet)

Microservicio FastAPI para **detección de anomalías “good-only”**:
- **Extractor**: DINOv2 ViT-S/14 (congelado, vía `timm`)
- **Memoria**: PatchCore (coreset k-center greedy + kNN con FAISS/sklearn)
- **Salidas**: `score` global, `heatmap` (PNG base64), `regions` (bboxes, contornos y áreas en px/mm²)
- **Persistencia** por `(role_id, roi_id)` en `models/`

> **Importante**: La **GUI WPF** debe enviar **ROI canónico** (recortado + rotado). El backend **no** recorta ni rota.

---

## Índice rápido

- [Instalación](#1-instalación)
- [Ejecución](#2-ejecución)
- [Endpoints](#3-endpoints)
- [Notas de diseño](#4-notas-de-diseño)
- [Integración con la GUI](#5-integración-con-la-gui-wpf-esquema)
- [Consejos de rendimiento](#6-consejos-de-rendimiento)

---

## 1) Instalación

```bash
python -m venv .venv
# Windows:
.venv\Scripts\activate
# Linux/macOS:
source .venv/bin/activate

pip install -r backend/requirements.txt
```

> **PyTorch** se descarga desde el índice oficial de PyTorch gracias a la opción `--extra-index-url` incluida en `requirements.txt`.
> Si tu red bloquea dominios externos, instala manualmente la rueda adecuada (`torch==2.1.2` CPU) y vuelve a ejecutar `pip install -r backend/requirements.txt`.
> Si `faiss-cpu` falla en tu plataforma, el servicio seguirá funcionando con `sklearn` (`NearestNeighbors`).
> Asegúrate de tener **Python 3.10+** y **pip** actualizado (`python -m pip install -U pip`).

---

## 2) Ejecución

```bash
uvicorn backend.app:app --reload
# por defecto: http://127.0.0.1:8000
# docs interactivos: http://127.0.0.1:8000/docs

# o con el script directo (respeta BDI_BACKEND_HOST/BDI_BACKEND_PORT o HOST/PORT; acepta BRAKEDISC_* por compatibilidad)
python backend/app.py
```

---

## 3) Endpoints

### `GET /health`
Comprueba estado y dispositivo.

**Response**
```json
{
  "status": "ok",
  "device": "cuda",
  "model": "vit_small_patch14_dinov2.lvd142m",
  "version": "0.1.0"
}
```

---

### `POST /fit_ok`  — *Acumula OKs y construye memoria (coreset + kNN)*
**Tipo**: `multipart/form-data`

**Campos**
- `role_id`: string  
- `roi_id`: string  
- `mm_per_px`: float (guardado para informes; no afecta a features)  
- `images`: uno o varios ficheros (PNG/JPG) de **ROI canónico** (tamaño libre; se reescala a múltiplo de 14 internamente)

**Ejemplo (curl)**
```bash
curl -X POST http://127.0.0.1:8000/fit_ok   -F role_id=Master1   -F roi_id=Pattern   -F mm_per_px=0.20   -F images=@roi_ok_01.png   -F images=@roi_ok_02.png
```

**Response**
```json
{
  "n_embeddings": 34992,
  "coreset_size": 700,
  "token_shape": [32, 32],
  "coreset_rate_requested": 0.02,
  "coreset_rate_applied": 0.018
}
```

---

### `POST /calibrate_ng` — *Fija umbral con 0–3 NG*
**Tipo**: `application/json`

**Body**
```json
{
  "role_id": "Master1",
  "roi_id": "Pattern",
  "mm_per_px": 0.20,
  "ok_scores": [12.1, 10.8, 11.5],
  "ng_scores": [28.4],         // opcional
  "area_mm2_thr": 1.0,         // opcional (filtro de islas)
  "score_percentile": 99       // opcional (p99 o p95)
}
```

**Response**
```json
{
  "threshold": 20.0,
  "p99_ok": 12.0,
  "p5_ng": 28.0,
  "mm_per_px": 0.2,
  "area_mm2_thr": 1.0,
  "score_percentile": 99
}
```

---

### `POST /infer` — *Inferencia y postproceso*
**Tipo**: `multipart/form-data`

**Campos**
- `role_id`, `roi_id`, `mm_per_px`
- `image`: PNG/JPG del **ROI canónico**
- `shape` (opcional, JSON como string) — **máscara del ROI**:  
  - Rect: `{"kind":"rect","x":0,"y":0,"w":384,"h":384}`  
  - Circle: `{"kind":"circle","cx":192,"cy":192,"r":180}`  
  - Annulus: `{"kind":"annulus","cx":192,"cy":192,"r":180,"r_inner":120}`

**Ejemplo (curl)**
```bash
curl -X POST http://127.0.0.1:8000/infer   -F role_id=Master1   -F roi_id=Pattern   -F mm_per_px=0.20   -F image=@roi_test.png   -F shape='{"kind":"circle","cx":192,"cy":192,"r":180}'
```

**Response (resumen)**
```json
{
  "role_id": "Master1",
  "roi_id": "Pattern",
  "score": 18.7,
  "threshold": 20.0,
  "heatmap_png_base64": "iVBORw0K...",
  "regions": [
    {"bbox":[x,y,w,h], "area_px": 250.0, "area_mm2": 10.0, "contour": [[x1,y1], ...]}
  ],
  "token_shape": [32, 32],
  "params": {
    "extractor": "vit_small_patch14_dinov2.lvd142m",
    "input_size": 448,
    "patch_size": 14,
    "coreset_rate": 0.02,
    "coreset_rate_applied": 0.018,
    "k": 1,
    "score_percentile": 99,
    "blur_sigma": 1.0,
    "mm_per_px": 0.2
  }
}
```

---

## 4) Notas de diseño

- **Extractor**: `vit_small_patch14_dinov2.lvd142m` (congelado, multi-capa con bloques 9 y 11).
  Entrada por defecto `448×448` (múltiplo de **14**); si envías `384×384`, se reescala internamente a múltiplo cercano.
- **Memoria**:
  - Embeddings por parche → **L2 normalize**
  - **Coreset** (k-center greedy) → 1–5% configurable (por defecto 2%)
  - Índice **FAISS** si disponible; si no, `sklearn.NearestNeighbors`.
- **Score**: percentil **p99** (configurable) del heatmap enmascarado.  
- **Umbral**:
  - Sin NG: **p99(OK)** (más un pequeño margen si lo deseas).
  - Con NG (0–3): entre **p99(OK)** y **p5(NG)**.
  - Si aún no se ha calibrado, el endpoint `/infer` devuelve `"threshold": null`.
- **Postproceso**: blur ligero, **máscara ROI**, eliminación de **islas < área_mm²** (convertido a px² con `mm_per_px`) y exporte de contornos/bboxes ordenados.
- **Persistencia**:
  - `models/<role>/<roi>/memory.npz` (embeddings coreset + `token_shape`)
  - `models/<role>/<roi>/index.faiss` (si FAISS)
  - `models/<role>/<roi>/calib.json` (umbral, p99_ok, p5_ng, mm_per_px, etc.)
- **Respuesta de `/infer`**: añade `params` con metadatos de extractor, coreset y configuración usada.
- **Configuración**: variables como `DEVICE`, `INPUT_SIZE`, `CORESET_RATE`, `MODELS_DIR` pueden definirse en un `.env` o como variables del sistema; usa el prefijo `BDI_` (por ejemplo `BDI_MODELS_DIR`, `BDI_CORESET_RATE`) y, por compatibilidad, también se aceptan los alias `BRAKEDISC_*`. Revisa `DEV_GUIDE.md` para detalles.

---

## 5) Integración con la GUI WPF (esquema)

- **/fit_ok**: enviar varios PNG/JPG de **ROI canónico** (recorte+rotación ya hechos) por cada `(role_id, roi_id)`.
- **/calibrate_ng**: tras recolectar `scores` (OK/NG), fijar umbral por `(role_id, roi_id)`.
- **/infer**: enviar ROI canónico y `shape` (opcional) para recibir `score`, `heatmap` (base64) y `regions`.
- **mm/px**: úsalo para convertir áreas a **mm²** y configurar el filtro de islas.

---

## 6) Consejos de rendimiento

- **CPU**: funciona con `sklearn` si no hay FAISS. Mantén **coreset_rate** bajo (1–2%).  
- **GPU**: acelera el extractor. FAISS también puede usar GPU si se habilita (no requerido).  
- **Batching**: por simplicidad se procesa imagen a imagen; puedes extender el endpoint si necesitas lote.

---

## 7) Solución de problemas

- **“timm no está disponible”** → revisa `pip install -r backend/requirements.txt`.  
- **Falla FAISS** → ignóralo; se usa `sklearn` automáticamente.  
- **Memoria no encontrada (infer)** → ejecuta **/fit_ok** primero para ese `(role_id, roi_id)`.  
- **Heatmap vacío o todo negro** → comprueba que el ROI sea correcto, la **máscara** no esté fuera de rango y que haya memoria suficiente (más OKs).  
- **Áreas en mm²** extrañas → verifica `mm_per_px` enviado por la GUI.

---

## 8) Cambios de configuración rápidos

- Modelo/tamaño de entrada: `backend/features.py` (`model_name`, `input_size` múltiplo de 14).
- Coreset rate: `PatchCoreMemory.build(..., coreset_rate=0.02)`.
- Percentil de score: `p_score` (en calib e infer).
- Filtro de islas: `area_mm2_thr` en calib/infer.

---

## 9) Estructura del proyecto

```
backend/
  app.py               # FastAPI: /fit_ok, /calibrate_ng, /infer, /health
  features.py          # DINOv2 ViT-S/14 congelado
  patchcore.py         # L2 normalize, coreset, kNN (FAISS/sklearn)
  infer.py             # pipeline de inferencia + posproceso
  calib.py             # cálculo de threshold
  roi_mask.py          # máscaras rect/circle/annulus
  storage.py           # persistencia en models/<role>/<roi>/
  utils.py             # helpers (I/O, base64, mm/px, percentiles)
  requirements.txt
models/
  <role_id>/<roi_id>/memory.npz
  <role_id>/<roi_id>/index.faiss   (opcional)
  <role_id>/<roi_id>/calib.json
```

---

## 10) Licencia y créditos
- Pesos **DINOv2** vía `timm` (respetar licencia del modelo).  
- Este backend es un esqueleto de referencia; adáptalo a tus requisitos de producción (logging, auth, trazas, etc.).
