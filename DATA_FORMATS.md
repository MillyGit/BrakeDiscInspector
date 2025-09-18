
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

### 1.3 `/match_one`

- **Método**: POST  
- **Tipo**: `multipart/form-data`  
- **Body**:
  - `template`: archivo de plantilla, o  
  - `file1` + `file2`: imágenes a comparar

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
  "model": "current_model.h5",
  "threshold": 0.57,
  "input_size": [224, 224],
  "status": "ready"
}
```

### 2.3 `/match_one`

```json
{
  "similarity": 0.94,
  "matched": true
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
    public double similarity { get; set; }
    public bool matched { get; set; }
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
