
# DATA_FORMATS — BrakeDiscInspector

Este documento define los formatos de datos usados en el sistema: requests/responses del backend, estructuras internas de la GUI y tipos de archivo soportados.

---

## 1) Formatos de request (Backend)

### 1.1 `/analyze`

- **Método**: POST  
- **Tipo**: `multipart/form-data`

#### Campos:
- `file`: PNG con el crop de ROI (requerido).
- `mask`: PNG binaria (opcional).
- `annulus`: JSON con parámetros de anillo (opcional).

Ejemplo:
```
multipart/form-data
├─ file: crop.png
├─ mask: mask.png
└─ annulus: {"cx":400,"cy":300,"ri":50,"ro":200}
```

### 1.2 `/train_status`

- **Método**: GET  
- **Tipo**: `application/json`  
- **Body**: vacío

### 1.3 `/match_master` (alias `/match_one`)

- **Método**: POST
- **Tipo**: `multipart/form-data`
- **Campos obligatorios**:
  - `image`: imagen donde buscar la plantilla.
  - `template`: plantilla (PNG en BGR o BGRA para máscara implícita).
- **Campos opcionales**:
  - `feature`, `thr`, `tm_thr`, `rot_range`, `scale_min`, `scale_max`.
  - `search_x`, `search_y`, `search_w`, `search_h` para limitar el área de búsqueda.
  - `debug` (`1/true`) para incluir capturas base64 de depuración.

---

## 2) Formatos de response (Backend)

### 2.1 `/analyze`

```json
{
  "label": "NG",
  "score": 0.83,
  "threshold": 0.57,
  "heatmap_png_b64": "iVBORw0KGgoAAAANS..."
}
```

### 2.2 `/train_status`

```json
{
  "state": "idle",
  "threshold": 0.57,
  "artifacts": {
    "model": {
      "path": ".../model/current_model.h5",
      "exists": true,
      "size_bytes": 7340032,
      "modified_at": 1717000200.123,
      "loaded": true
    },
    "threshold": {
      "path": ".../model/threshold.txt",
      "exists": true,
      "size_bytes": 6,
      "modified_at": 1717000200.456,
      "value": 0.57,
      "source": "model_cache"
    },
    "log": {
      "path": ".../model/logs/train.log",
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
    "layers_preview": ["input", "conv1", "conv2", "dense", "logits", "..."],
    "input_shape": [null, 600, 600, 3],
    "output_shape": [null, 1],
    "trainable_params": 123456,
    "non_trainable_params": 2048
  }
}
```

### 2.3 `/match_master` (alias `/match_one`)

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
    "matches": 480,
    "good": 132,
    "confidence": 0.86
  }
}
```

---

## 3) Estructuras internas (GUI)

### 3.1 ROI

```csharp
class ROI {
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double AngleDeg { get; set; }
    public string Legend { get; set; }
}
```

### 3.2 Respuesta de análisis

```csharp
class AnalyzeResponse {
    public string label { get; set; }
    public double score { get; set; }
    public double threshold { get; set; }
    public string heatmap_png_b64 { get; set; }
}
```

### 3.3 MatchingResponse

```csharp
class MatchingResponse {
    public bool found { get; set; }
    public string stage { get; set; }
    public double center_x { get; set; }
    public double center_y { get; set; }
    public double confidence { get; set; }
    public double tm_best { get; set; }
    public double tm_thr { get; set; }
    public MatchingDebug debug { get; set; } // opcional cuando se solicita
}

class MatchingDebug {
    public string gray_png { get; set; }
    public string tpl_gray_png { get; set; }
    public MatchingStats stats { get; set; }
}

class MatchingStats {
    public double gray_mean { get; set; }
    public double gray_std { get; set; }
}
```

---

## 4) Tipos de archivo soportados

- **Imágenes entrada**: BMP, PNG, JPG (convertidas a Mat en GUI).  
- **Formato transmisión**: PNG (cropped, rotado).  
- **Máscaras**: PNG binaria (0/255).  
- **Annulus**: JSON.  
- **Modelo**: TensorFlow `.h5`.  
- **Heatmap**: PNG codificado en Base64.  

---

## 5) Convenciones

- Encoding JSON: siempre UTF‑8.  
- Precision de `score` y `threshold`: 3 decimales típicamente (`F3`).  
- Todas las coordenadas (`X`, `Y`, etc.) en píxeles de la imagen original.  

---

## 6) Referencias cruzadas

- **API_REFERENCE.md** → contratos de endpoints  
- **ROI_AND_MATCHING_SPEC.md** → geometría de ROI  
- **ARCHITECTURE.md** → flujo de datos  
