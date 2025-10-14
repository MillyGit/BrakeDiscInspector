# 📌 Actualización — 2025-10-07

**Cambios clave documentados en esta versión:**
- Se unifica el flujo de onboarding para backend (FastAPI PatchCore) y GUI (WPF) con énfasis en datasets `datasets/<role>/<roi>/<ok|ng>/`.
- Se añaden referencias explícitas a los módulos de código vigentes (`app.py`, `InferenceEngine`, `BackendClient`, `DatasetManager`).
- Se consolida el plan de pruebas mínimas (smoke + GUI) y variables de entorno utilizadas en 2025.

# DEV_GUIDE — BrakeDiscInspector

Guía de desarrollo para configurar, extender y validar BrakeDiscInspector en entornos locales. Cubre el backend FastAPI (PatchCore + DINOv2) y la GUI WPF.

---

## Índice rápido

- [Preparación del repositorio](#1-preparación-del-repositorio)
- [Backend (Python)](#2-backend-python)
- [GUI (WPF)](#3-gui-wpf)
- [Scripts auxiliares](#4-scripts-auxiliares)
- [Estándares de código](#5-estándares-de-código)
- [Testing](#6-testing)
- [Roadmap sugerido](#7-roadmap-sugerido)
- [Referencias cruzadas](#8-referencias-cruzadas)

---

## 1) Preparación del repositorio

```bash
git clone https://github.com/<usuario>/BrakeDiscInspector.git
cd BrakeDiscInspector
```

Estructura general:
```
backend/   # Microservicio FastAPI (PatchCore + DINOv2)
gui/       # WPF (.NET 8) para gestión de ROI/datasets
docs/      # MCP y documentación extendida
scripts/   # utilidades (PowerShell)
```

---

## 2) Backend (Python)

### 2.1 Requisitos
- Python 3.10 o superior
- `pip` actualizado (`python -m pip install -U pip`)
- GPU opcional (funciona en CPU; se detecta con `torch.cuda.is_available()`)

### 2.2 Instalación
```bash
cd backend
python -m venv .venv
source .venv/bin/activate      # PowerShell: .venv\Scripts\Activate.ps1
pip install -r requirements.txt
```

`requirements.txt` incluye PyTorch 2.x, `timm`, `opencv-python`, `faiss-cpu` (opcional), `fastapi`, `uvicorn`, `scikit-learn` y utilidades NumPy/SciPy.

### 2.3 Archivos clave
- `app.py` — Define `/health`, `/fit_ok`, `/calibrate_ng`, `/infer` y orquesta la persistencia.【F:backend/app.py†L1-L199】
- `features.py` — Wrapper `DinoV2Features` (modelo `vit_small_patch14_dinov2.lvd142m`, resize a múltiplos de 14).【F:backend/features.py†L1-L200】
- `patchcore.py` — Implementa `PatchCoreMemory.build` y el kNN (FAISS opcional).【F:backend/patchcore.py†L1-L200】
- `infer.py` — `InferenceEngine.run` calcula heatmap, score (p99), aplica máscara y genera regiones/contornos.【F:backend/infer.py†L17-L181】
- `calib.py` — `choose_threshold` mezcla percentiles OK/NG y guarda `calib.json`.【F:backend/calib.py†L1-L120】
- `storage.py` — Persistencia en `models/<role>/<roi>/` para `memory.npz`, `index.faiss`, `calib.json`.【F:backend/storage.py†L1-L80】

### 2.4 Ejecución en desarrollo
```bash
uvicorn backend.app:app --reload --host 127.0.0.1 --port 8000
# Documentación interactiva: http://127.0.0.1:8000/docs
```

### 2.5 Smoke tests rápidos
```bash
curl http://127.0.0.1:8000/health
curl -X POST http://127.0.0.1:8000/fit_ok -F role_id=Demo -F roi_id=ROI1 -F mm_per_px=0.20 -F images=@sample_ok.png
curl -X POST http://127.0.0.1:8000/infer -F role_id=Demo -F roi_id=ROI1 -F mm_per_px=0.20 -F image=@sample_ok.png
```

Los artefactos generados se almacenan en `backend/models/Demo/ROI1/`.

### 2.6 Depuración
- Ajustar `uvicorn` con `--log-level debug` para inspeccionar llamadas.
- Revisar trazas en las respuestas JSON (`{"error","trace"}`) cuando hay excepciones.
- Ejecutar pruebas unitarias/funcionales en `backend/tests/` (si existen) con `pytest`.

### 2.7 Variables de entorno útiles

| Variable | Uso | Comentario |
|----------|-----|------------|
| `DEVICE` | Forzar `cpu`/`cuda`. | Por defecto el extractor elige automáticamente. |
| `CORESET_RATE` | Ajusta proporción del coreset (0.02 por defecto). | Se lee al construir la memoria; valores altos consumen más RAM. |
| `INPUT_SIZE` | Tamaño de entrada del extractor (múltiplo de 14, por defecto 448). | Cambiarlo altera `token_shape`. |
| `MODELS_DIR` | Carpeta raíz de artefactos persistidos. | Permite montar almacenamiento externo. |
| `BRAKEDISC_BACKEND_HOST` / `PORT` | Host/puerto usados cuando se ejecuta `python app.py`. | Facilita desplegar en LAN. |

---

## 3) GUI (WPF)

### 3.1 Requisitos
- Windows 10/11
- Visual Studio 2022 o superior
- .NET SDK 8.0
- Paquetes NuGet principales: `OpenCvSharp4`, `OpenCvSharp4.runtime.win`, `OpenCvSharp4.Extensions`, `CommunityToolkit.Mvvm`.

### 3.2 Archivos principales
- `MainWindow.xaml` / `.cs` — Pestañas Dataset/Train/Infer, binding y comandos.
- `Workflow/BackendClient.cs` — Cliente HTTP async (fit/calibrate/infer) con DTOs resilientes.【F:gui/BrakeDiscInspector_GUI_ROI/Workflow/BackendClient.cs†L20-L218】
- `Workflow/DatasetManager.cs` — Exporta PNG + metadata JSON (`shape_json`, `mm_per_px`, `angle`).【F:gui/BrakeDiscInspector_GUI_ROI/Workflow/DatasetManager.cs†L18-L80】
- `Workflow/DatasetSample.cs` — Carga thumbnails y `SampleMetadata` (timestamp, origen).【F:gui/BrakeDiscInspector_GUI_ROI/Workflow/DatasetSample.cs†L1-L120】
- `ROI/*.cs`, `RoiOverlay.cs` — Adorners y sincronización (no modificar geometría).【F:gui/BrakeDiscInspector_GUI_ROI/RoiAdorner.cs†L1-L200】

### 3.3 Configuración
`appsettings.json` de ejemplo:
```json
{
  "Backend": {
    "BaseUrl": "http://127.0.0.1:8000",
    "DatasetRoot": "C:\\data\\brakedisc\\datasets",
    "MmPerPx": 0.20
  }
}
```

Variables de entorno opcionales: `BRAKEDISC_BACKEND_BASEURL`, `BRAKEDISC_BACKEND_HOST`, `BRAKEDISC_BACKEND_PORT` para sobreescribir la URL sin recompilar.

### 3.4 Flujo recomendado
1. Cargar imagen y dibujar ROI (rect/círculo/annulus) con los adorners existentes.
2. Exportar ROI canónico con **Add OK/NG from Current ROI** (genera PNG + `.json`).
3. Entrenar memoria con **Train memory (`/fit_ok`)** y revisar respuesta (`n_embeddings`, `coreset_size`, `token_shape`).
4. Recolectar scores y ejecutar **Calibrate (`/calibrate_ng`)** para fijar `threshold` y `area_mm2_thr`.
5. Evaluar la imagen actual con **Infer current ROI**, superponer el heatmap y revisar `regions`.

### 3.5 Depuración GUI
- Usar los logs de la vista (`AppendLog` o similar) para registrar request/response (incluyendo `X-Correlation-Id`).
- Verificar overlays cargando el PNG devuelto (`heatmap_png_base64`) en un `ImageBrush` con la misma anchura/altura que el ROI canónico.
- Manejar errores HTTP (`HttpRequestException`) mostrando mensajes claros al usuario y detalles en el panel de logs.

---

## 4) Scripts auxiliares

| Script | Descripción |
|--------|-------------|
| `scripts/setup_dev.ps1` | Crea entorno virtual y ejecuta `pip install -r backend/requirements.txt`. |
| `scripts/run_backend.ps1` | Activa la venv y lanza `uvicorn backend.app:app`. |
| `scripts/run_gui.ps1` | Abre la solución WPF en Visual Studio. |
| `scripts/check_env.ps1` | Comprueba dependencias (Python, dotnet, git). |

Ejecutar con `PowerShell` habilitando `Set-ExecutionPolicy -Scope Process RemoteSigned` si es necesario.

---

## 5) Estándares de código

- **Python**: seguir PEP8, usar `typing` y logging estructurado (`logging.getLogger(__name__)`).
- **C#**: convenciones .NET, `async/await` para I/O, aprovechar `ObservableCollection` para listas en UI.
- **Commits**: idioma inglés, formato `type(scope): resumen` (estilo Conventional Commits).

---

## 6) Testing

### 6.1 Backend
- Ejecutar smoke tests (`/health`, `/fit_ok`, `/infer`).
- Añadir pruebas en `backend/tests/` para `PatchCoreMemory`, `InferenceEngine` o utilidades cuando se modifique su comportamiento.

### 6.2 GUI
- Compilar solución (`Build > Build Solution`).
- Validar el flujo completo Dataset → `/fit_ok` → `/calibrate_ng` → `/infer` usando muestras de ejemplo (OK/NG).
- Confirmar que el heatmap permanece alineado tras redimensionar la ventana o recargar imagen.

---

## 7) Roadmap sugerido

- [ ] Automatizar `pytest` y análisis estático (GitHub Actions).
- [ ] Persistir manifiestos por ROI (`datasets/<role>/<roi>/manifest.json`).
- [ ] Incorporar exportación de reportes (CSV/JSON) desde la GUI.
- [ ] Añadir métricas Prometheus en el backend (`prometheus_fastapi_instrumentator`).
- [ ] Implementar modo batch en la GUI manteniendo restricciones de adorners.

---

## 8) Referencias cruzadas

- [README.md](README.md) — Visión general y quick start.
- [ARCHITECTURE.md](ARCHITECTURE.md) — Componentes y diagramas.
- [API_REFERENCE.md](API_REFERENCE.md) — Contratos HTTP detallados.
- [DATA_FORMATS.md](DATA_FORMATS.md) — Esquemas de requests/responses y archivos.
- [DEPLOYMENT.md](DEPLOYMENT.md) — Guía de despliegue y smoke tests.
- [LOGGING.md](LOGGING.md) — Política de observabilidad.
- [docs/mcp/overview.md](docs/mcp/overview.md) — Plan de coordinación y responsabilidades.
