
# API_REFERENCE — BrakeDiscInspector

Este documento describe los endpoints HTTP expuestos por el backend Flask de BrakeDiscInspector, su formato de entrada/salida y ejemplos de uso.

---

## 1) Endpoints principales

### 1.1 `/analyze` (POST)

- **Descripción**: Analiza un crop de disco de freno y devuelve la predicción junto con un heatmap.
- **URL**: `http://<host>:5000/analyze`
- **Método**: POST
- **Content-Type**: `multipart/form-data`

#### Parámetros de entrada

- `file` (requerido): PNG del ROI ya recortado y rotado por la GUI.
- `mask` (opcional): PNG binaria (mismo tamaño que el ROI) para anular píxeles fuera de la máscara.
- `annulus` (opcional): JSON con los parámetros de un anillo de centrado.

Ejemplo JSON de annulus:
```json
{
  "cx": 400,
  "cy": 300,
  "ri": 50,
  "ro": 200
}
```

#### Respuesta (200 OK)

```json
{
  "label": "NG",
  "score": 0.83,
  "threshold": 0.57,
  "heatmap_png_b64": "iVBORw0KGgoAAAANS..."
}
```

- `label`: `"OK"` o `"NG"`
- `score`: valor de 0.0 a 1.0 (score de defecto)
- `threshold`: umbral leído de `threshold.txt`
- `heatmap_png_b64`: heatmap de Grad-CAM codificado en PNG y Base64

#### Errores

- 400 Bad Request: si falta el archivo o parámetros incorrectos.
- 500 Internal Server Error: error en inferencia u otro fallo interno.

Ejemplo con `curl`:
```bash
curl -X POST http://127.0.0.1:5000/analyze   -F "file=@crop.png"   -F "annulus={"cx":400,"cy":300,"ri":50,"ro":200}"
```

---

### 1.2 `/train_status` (GET)

- **Descripción**: Informa del estado actual del modelo cargado.
- **URL**: `http://<host>:5000/train_status`
- **Método**: GET

#### Respuesta (200 OK)
```json
{
  "state": "idle",
  "threshold": 0.57,
  "pid": 9124,
  "artifacts": {
    "model": {
      "path": "/app/backend/model/current_model.h5",
      "exists": true,
      "size_bytes": 7340032,
      "modified_at": 1717000200.123,
      "loaded": true
    },
    "threshold": {
      "path": "/app/backend/model/threshold.txt",
      "exists": true,
      "size_bytes": 6,
      "modified_at": 1717000200.456,
      "value": 0.57,
      "source": "model_cache"
    },
    "log": {
      "path": "/app/backend/model/logs/train.log",
      "exists": true,
      "size_bytes": 10240,
      "modified_at": 1717000200.789,
      "tail": "Epoch 5/20 - val_loss=0.21..."
    }
  },
  "model_runtime": {
    "loaded": true,
    "name": "classifier",
    "layer_count": 5,
    "layers_preview": [
      "input",
      "conv1",
      "conv2",
      "dense",
      "logits",
      "..."
    ],
    "input_shape": [null, 600, 600, 3],
    "output_shape": [null, 1],
    "trainable_params": 123456,
    "non_trainable_params": 2048
  },
  "log_tail": "Epoch 5/20 - val_loss=0.21..."
}
```

> Los valores numéricos (`size_bytes`, `modified_at`, etc.) variarán en función de tu despliegue.

---

### 1.3 `/match_master` (POST) — alias `/match_one`

- **Descripción**: Matching maestro que localiza la plantilla (`template`) dentro de una imagen (`image`).
- **URL**: `http://<host>:5000/match_master` (compatible con `/match_one`).
- **Método**: POST

#### Entrada (`multipart/form-data`)
- `image`: captura donde buscar (obligatoria).
- `template`: plantilla de referencia (obligatoria, se acepta PNG con canal alfa para máscara implícita).
- Parámetros opcionales:
  - `feature`: `auto` (por defecto) / `sift` / `orb` / `tm_rot` / `geom`.
  - `thr`: umbral de confianza para coincidencia geométrica/SIFT-ORB (por defecto `0.6`).
  - `tm_thr`: umbral de template matching (`0.8` por defecto).
  - `rot_range`: rango de rotaciones ±grados para `tm_rot`.
  - `scale_min` / `scale_max`: escalas mín./máx. para `tm_rot`.
  - `search_x`, `search_y`, `search_w`, `search_h`: ROI para limitar la búsqueda.
  - `debug`: `1/true` para adjuntar capturas de depuración codificadas en Base64.

#### Respuesta (ejemplo `found=true`)
```json
{
  "found": true,
  "stage": "TM_OK",
  "center_x": 412.5,
  "center_y": 298.0,
  "confidence": 0.91,
  "tm_best": 0.91,
  "tm_thr": 0.8,
  "bbox": [380.0, 260.0, 65.0, 76.0],
  "sift_orb": {
    "detector": "auto->orb",
    "kp_tpl": 523,
    "kp_img": 497,
    "matches": 480,
    "good": 132,
    "confidence": 0.86
  }
}
```

#### Respuesta (ejemplo `found=false`)
```json
{
  "found": false,
  "stage": "TM_FAIL",
  "reason": "tm_below_threshold",
  "tm_best": 0.42,
  "tm_thr": 0.8,
  "crop_off": [0, 0],
  "sift_orb": {
    "detector": "auto->orb",
    "matches": 12,
    "good": 1,
    "fail_reason": "not_enough_good_matches"
  }
}
```

---

## 2) Convenciones generales

- **Formato de imágenes**: se espera PNG en BGR (decodificado por OpenCV).
- **Máscaras**: PNG binaria o annulus JSON. Solo se aplicará una de ellas si ambas se envían.
- **Decisión**: `"NG"` si `score ≥ threshold`; de lo contrario `"OK"`.

---

## 3) Ejemplos de uso

### 3.1 Análisis simple desde GUI
- GUI rota ROI → envía `file=crop.png`
- Backend devuelve `label/score/threshold/heatmap`

### 3.2 Uso con máscara
```bash
curl -X POST http://127.0.0.1:5000/analyze   -F "file=@crop.png"   -F "mask=@mask.png"
```

### 3.3 Solo consulta de estado
```bash
curl http://127.0.0.1:5000/train_status
```

---

## 4) Referencias cruzadas

- **README.md** → descripción general
- **ARCHITECTURE.md** → flujo GUI↔backend
- **ROI_AND_MATCHING_SPEC.md** → detalles de ROI y annulus
- **DATA_FORMATS.md** → formatos internos
