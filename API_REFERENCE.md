
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
  "model": "current_model.h5",
  "threshold": 0.57,
  "input_size": [224, 224],
  "status": "ready"
}
```

---

### 1.3 `/match_one` (POST)

- **Descripción**: Endpoint experimental para matching basado en plantilla o comparación directa de archivos.
- **URL**: `http://<host>:5000/match_one`
- **Método**: POST

#### Entrada
- `template` o `file1` + `file2`

#### Respuesta
```json
{
  "similarity": 0.94,
  "matched": true
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
