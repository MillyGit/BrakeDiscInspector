
# BrakeDiscInspector

**BrakeDiscInspector** es un sistema completo de **inspección de discos de freno** mediante visión artificial.  
El proyecto se compone de:

- Un **backend en Python (Flask + TensorFlow + OpenCV)** para la detección de defectos, clasificación y generación de mapas de calor.
- Una **interfaz gráfica (GUI en C# / WPF con OpenCvSharp)** que permite al usuario cargar imágenes, dibujar regiones de interés (ROI), rotarlas, enviar recortes al backend y visualizar resultados con etiquetas, puntuaciones y mapas de calor.

El entorno está documentado y estructurado para ser **Codex-ready**: cualquier agente de IA puede navegar el repo, encontrar los puntos de anclaje y modificar o extender el sistema sin ambigüedades.

---

## ✨ Características principales

- **Detección de defectos** en discos de freno con modelos de deep learning (TensorFlow).
- **Comunicación HTTP** entre GUI y backend (endpoint `/analyze` y otros auxiliares).
- **Rotación interactiva del ROI** en la GUI con adorners personalizados.
- **Letterboxing automático** para mantener la relación de aspecto entre imagen original y canvas.
- **Visualización de resultados**:
  - Etiqueta (OK / NG o clase de defecto).
  - Score numérico y umbral.
  - Heatmap en tiempo real superpuesto a la ROI.
- **Arquitectura extensible** con documentación detallada en Markdown:
  - `ARCHITECTURE.md`
  - `API_REFERENCE.md`
  - `ROI_AND_MATCHING_SPEC.md`
  - `DATA_FORMATS.md`
  - `DEV_GUIDE.md`
  - `DEPLOYMENT.md`
  - `LOGGING.md`
  - `CONTRIBUTING.md`

---

## 📂 Estructura del proyecto

```
BrakeDiscInspector/
├─ backend/                 # Backend Flask (detección)
│  ├─ app.py
│  ├─ requirements.txt
│  ├─ environment.yml
│  └─ utils/
├─ gui/                     # GUI en WPF (C# + OpenCvSharp)
│  ├─ BrakeDiscInspector_GUI_ROI.sln
│  └─ BrakeDiscInspector_GUI_ROI/
│     ├─ App.xaml / App.xaml.cs
│     ├─ MainWindow.xaml / MainWindow.xaml.cs
│     ├─ BackendAPI.cs
│     ├─ ROI/
│     │  ├─ ROI.cs
│     │  ├─ RoiAdorner.cs
│     │  └─ RoiRotateAdorner.cs
│     └─ Overlays/
│        └─ RoiOverlay.cs
├─ scripts/                 # utilidades PowerShell
│  ├─ setup_dev.ps1
│  ├─ run_backend.ps1
│  ├─ run_gui.ps1
│  ├─ check_env.ps1
│  └─ export_onnx.ps1
├─ README.md                # este archivo
├─ ARCHITECTURE.md
├─ API_REFERENCE.md
├─ ROI_AND_MATCHING_SPEC.md
├─ DATA_FORMATS.md
├─ DEV_GUIDE.md
├─ DEPLOYMENT.md
├─ LOGGING.md
├─ CONTRIBUTING.md
├─ .gitignore
└─ .editorconfig
```

---

## 🚀 Instalación y uso

### Backend (Python)

1. Entra a la carpeta `backend/`:
   ```bash
   cd backend
   python -m venv .venv
   .venv\Scripts\activate   # en Windows
   pip install -r requirements.txt
   ```
2. Coloca tu modelo entrenado en:
   ```
   backend/model/current_model.h5
   backend/model/threshold.txt   # umbral, ej. 0.57
   ```
3. Arranca el servidor:
   ```bash
   python app.py
   ```
4. Comprueba:
   ```bash
   curl http://127.0.0.1:5000/train_status
   ```

### GUI (WPF en C#)

1. Abre `gui/BrakeDiscInspector_GUI_ROI.sln` en Visual Studio.
2. Instala los paquetes NuGet necesarios:
   - `OpenCvSharp4`
   - `OpenCvSharp4.runtime.win`
   - `OpenCvSharp4.Extensions`
3. Configura `appsettings.json`:
   ```json
   {
     "Backend": { "BaseUrl": "http://127.0.0.1:5000" }
   }
   ```
4. Compila y ejecuta:
   - Carga una imagen.
   - Dibuja ROI (mínimo 10x10).
   - Rota ROI con el adorner de rotación.
   - Pulsa **Analyze** → la GUI envía el crop rotado al backend, recibe etiqueta/score/threshold y muestra el heatmap.

---

## 🔗 API disponible

### `/analyze` (POST)
- Entrada: imagen PNG de la ROI, opcional máscara/annulus.
- Salida:
  ```json
  {
    "label": "NG",
    "score": 0.83,
    "threshold": 0.57,
    "heatmap_png_b64": "..."
  }
  ```

### `/train_status` (GET)
- Retorna info del modelo cargado y metadata de artefactos (tamaño, timestamp, tail del log, threshold cargado, etc.).

### `/match_one` (POST)
- (Opcional) Matching por plantilla o ficheros.

> Detalles completos en [API_REFERENCE.md](API_REFERENCE.md)

---

## 📐 ROI y matching

- ROI definido por: `X`, `Y`, `Width`, `Height`, `AngleDeg`, `Legend`.
- ROI mínimo: **10x10 píxeles**.
- Rotación en tiempo real vía adorner.
- Annulus opcional para centrado de disco.
- Letterbox garantiza la coherencia imagen→canvas.

Más detalles en [ROI_AND_MATCHING_SPEC.md](ROI_AND_MATCHING_SPEC.md).

---

## 📑 Documentación adicional

- **[ARCHITECTURE.md](ARCHITECTURE.md)** → flujo GUI/Backend, diagrama mermaid.
- **[API_REFERENCE.md](API_REFERENCE.md)** → endpoints, contratos, ejemplos curl.
- **[ROI_AND_MATCHING_SPEC.md](ROI_AND_MATCHING_SPEC.md)** → definición ROI, rotación, annulus.
- **[DATA_FORMATS.md](DATA_FORMATS.md)** → formatos de requests/responses.
- **[DEV_GUIDE.md](DEV_GUIDE.md)** → setup local completo.
- **[DEPLOYMENT.md](DEPLOYMENT.md)** → despliegue local/prod y tests.
- **[LOGGING.md](LOGGING.md)** → niveles y rutas de logs.
- **[CONTRIBUTING.md](CONTRIBUTING.md)** → normas para contribuir.

---

## ✅ Checklist para Codex

- [x] Documentación enlazada desde este README.  
- [x] Estructura clara: backend, gui, scripts, docs.  
- [x] Explicación de cada endpoint y flujo GUI-backend.  
- [x] Instrucciones de instalación y ejecución.  
- [x] Convenciones de ROI y rotación descritas.  
- [x] Scripts auxiliares (`setup_dev.ps1`, `run_backend.ps1`, etc.).  

Con esto, cualquier agente Codex puede navegar el proyecto, entender los componentes y modificarlos sin ambigüedad.

## Para Codex
- [Arquitectura](ARCHITECTURE.md)
- [API](API_REFERENCE.md)
- [ROI / Matching](ROI_AND_MATCHING_SPEC.md)
- [Datos](DATA_FORMATS.md)
- [Dev Guide](DEV_GUIDE.md)
- [Despliegue](DEPLOYMENT.md)
- [Logging](LOGGING.md)
- [Contribución](CONTRIBUTING.md)


---
