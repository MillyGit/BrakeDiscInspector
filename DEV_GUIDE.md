
# DEV_GUIDE — BrakeDiscInspector

Guía de desarrollo para configurar, extender y probar el proyecto en entornos locales.  
Cubre **backend (Python)** y **GUI (C# / WPF)**, así como scripts auxiliares.

---

## 1) Clonar y preparar entorno

```bash
git clone https://github.com/<usuario>/BrakeDiscInspector.git
cd BrakeDiscInspector
```

Estructura básica:
```
backend/   # Flask + TensorFlow + OpenCV
gui/       # WPF .NET + OpenCvSharp
scripts/   # utilidades PowerShell
docs/      # Markdown (especificaciones)
```

---

## 2) Backend (Python)

### 2.1 Requisitos
- Python 3.10+  
- pip + virtualenv  
- GPU opcional: CUDA/cuDNN instalados

### 2.2 Instalación
```bash
cd backend
python -m venv .venv
.venv\Scripts\activate   # en Windows
pip install -r requirements.txt
```

### 2.3 Archivos importantes
- `app.py`: entrypoint Flask, define endpoints `/analyze`, `/train_status`, `/match_master` (alias `/match_one`)
- `model/current_model.h5`: modelo entrenado
- `model/threshold.txt`: umbral NG/OK
- `utils/`: funciones auxiliares (letterbox, annulus, heatmap, schemas)

### 2.4 Ejecución
```bash
python app.py
```
Por defecto en `http://127.0.0.1:5000`.

### 2.5 Tests básicos
```bash
curl http://127.0.0.1:5000/train_status
curl -X POST http://127.0.0.1:5000/analyze -F "file=@samples/crop.png"
```

---

## 3) GUI (WPF en C#)

### 3.1 Requisitos
- Windows 10/11
- Visual Studio 2022
- .NET SDK 8.0
- Paquetes NuGet:
  - `OpenCvSharp4`
  - `OpenCvSharp4.runtime.win`
  - `OpenCvSharp4.Extensions`

### 3.2 Archivos principales
- `MainWindow.xaml` / `MainWindow.xaml.cs`: UI principal
- `BackendAPI.cs`: cliente HTTP tipado
- `ROI/`: lógica de ROIs y adorners
- `Overlays/RoiOverlay.cs`: overlay sincronizado con canvas

### 3.3 Configuración
`appsettings.json` (GUI):
```json
{
  "Backend": { "BaseUrl": "http://127.0.0.1:5000" }
}
```

### 3.4 Flujo típico
1. Abrir GUI y cargar imagen (`File > Open`).
2. Dibujar ROI en canvas (mín. 10x10).
3. Rotar ROI con adorner circular.
4. Pulsar **Analyze** → la GUI genera crop rotado y lo envía al backend.
5. GUI muestra label/score/threshold y heatmap.

---

## 4) Scripts auxiliares (PowerShell)

- `scripts/setup_dev.ps1`: crea venv, instala dependencias
- `scripts/run_backend.ps1`: activa venv y lanza Flask
- `scripts/run_gui.ps1`: abre solución en VS
- `scripts/check_env.ps1`: verifica presencia de Python, .NET y dependencias
- `scripts/export_onnx.ps1`: convierte modelo `.h5` → `.onnx`

Ejemplo:
```powershell
.\scripts\setup_dev.ps1
.\scriptsun_backend.ps1
```

---

## 5) Debugging

### 5.1 Backend
- Logs en `backend/logs/backend.log`
- Nivel DEBUG activa trazas detalladas
- `Ctrl+C` detiene servidor

### 5.2 GUI
- `AppendLog` escribe en `gui/logs/gui.log`
- Verificar alineación ROI ↔ Canvas (`SyncOverlayToImage()`)
- Errores comunes:
  - CS1674 → no usar `using` con `ImEncode`
  - CS1061 → usar `BitmapSourceConverter` en vez de `ToWriteableBitmap`

---

## 6) Extender funcionalidad

- **Nuevos endpoints**: añadir en `backend/app.py`, documentar en `API_REFERENCE.md`
- **Nuevos tipos de ROI**: extender `ROI.cs`, `RoiOverlay.cs`
- **Máscaras avanzadas**: aceptar múltiples ficheros en `/analyze`
- **UI extra**: añadir pestañas WPF y bindings en `MainWindow.xaml`

---

## 7) Convenciones de código

- Backend Python: PEP8, logging estructurado, funciones puras en `utils/`
- GUI C#: PascalCase para clases/métodos, camelCase para campos privados
- Commits: mensajes en inglés, claro y conciso

---

## 8) Roadmap sugerido

- [ ] Multiples ROIs simultáneos
- [ ] Batch analyze
- [ ] Entrenamiento incremental
- [ ] Exportación resultados a CSV/DB
- [ ] Integración Prometheus/Grafana

---

## 9) Recursos cruzados

- **ARCHITECTURE.md**: diagrama flujo
- **API_REFERENCE.md**: contratos endpoints
- **ROI_AND_MATCHING_SPEC.md**: geometría ROI
- **DATA_FORMATS.md**: formatos request/response
- **DEPLOYMENT.md**: despliegue en LAN/prod
- **LOGGING.md**: política de logs
