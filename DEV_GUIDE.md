
# 📌 Actualización — 2025-10-07

**Cambios clave (GUI):**
- Corrección de salto del frame al clicar adorner (círculo/annulus): cálculo y propagación del centro reales en `SyncModelFromShape` y sincronización `X,Y = CX,CY` en `CreateLayoutShape`.
- Bbox SIEMPRE cuadrado para circle/annulus; overlay heatmap alineado.
- Decisiones del proyecto y parámetros vigentes documentados.

**Cambios clave (Backend):**
- PatchCore + DINOv2 ViT-S/14; endpoints `/health`, `/fit_ok`, `/calibrate_ng`, `/infer`; persistencia por `(role_id, roi_id)`.

# DEV_GUIDE — BrakeDiscInspector

Guía de desarrollo para configurar, extender y probar el proyecto en entornos locales. Cubre el backend FastAPI (PatchCore + DINOv2) y la GUI WPF.

---

## Índice rápido

- [Preparación del repositorio](#1-preparación-del-repositorio)
- [Backend (Python)](#2-backend-python)
- [GUI (WPF)](#3-gui-wpf)
- [Scripts auxiliares](#4-scripts-auxiliares-scripts)
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
scripts/   # utilidades (PowerShell)
docs/      # documentación (arquitectura, MCP, etc.)
```

---

## 2) Backend (Python)

### 2.1 Requisitos
- Python 3.10 o superior
- `pip` actualizado (`python -m pip install -U pip`)
- GPU opcional (CUDA/cuDNN) — funciona también en CPU

### 2.2 Instalación
```bash
cd backend
python -m venv .venv
source .venv/bin/activate      # PowerShell: .venv\Scripts\Activate.ps1
pip install -r requirements.txt
```

> El archivo `requirements.txt` incluye `torch`, `timm`, `opencv-python`, `faiss-cpu` (opcional) y `fastapi`.

### 2.3 Archivos clave
- `app.py` — endpoints `/health`, `/fit_ok`, `/calibrate_ng`, `/infer`.
- `features.py` — wrapper DINOv2 (`vit_small_patch14_dinov2.lvd142m`).
- `patchcore.py` — memoria PatchCore (coreset + kNN).
- `infer.py` — pipeline de inferencia y posprocesado (heatmap, contornos).
- `calib.py` — selección de umbral con percentiles.
- `storage.py` — persistencia en `models/<role>/<roi>/`.
- `README_backend.md` — guía operativa y ejemplos `curl`.

### 2.4 Ejecución en desarrollo
```bash
uvicorn backend.app:app --reload --port 8000
# Documentación interactiva: http://127.0.0.1:8000/docs
```

### 2.5 Pruebas rápidas
```bash
curl http://127.0.0.1:8000/health
curl -X POST http://127.0.0.1:8000/fit_ok -F role_id=Demo -F roi_id=ROI1 -F mm_per_px=0.2 -F images=@sample_ok.png
curl -X POST http://127.0.0.1:8000/infer -F role_id=Demo -F roi_id=ROI1 -F mm_per_px=0.2 -F image=@sample_ok.png
```

> Los comandos anteriores generan/usan artefactos en `backend/models/Demo/ROI1/`.

### 2.6 Debugging
- Activar logs (`uvicorn --log-level debug`).
- Revisar `logging` dentro de `app.py` y `infer.py` para tiempos parciales.
- Ejecutar scripts/unit tests en `backend/tests/` para validar componentes aislados.

### 2.7 Variables de entorno útiles

| Variable | Uso | Comentario |
|----------|-----|------------|
| `DEVICE` | Forzar `cpu`/`cuda`. | Por defecto se autodetecta; útil cuando la GPU está ocupada. |
| `CORESET_RATE` | Ajustar tamaño del coreset. | Acepta valores 0.01–0.05; comprueba memoria disponible antes de subirlo. |
| `INPUT_SIZE` | Cambiar tamaño de entrada (múltiplo de 14). | Impacta en `token_shape` y en los heatmaps devueltos. |
| `MODELS_DIR` | Definir carpeta de modelos. | Permite montar almacenamiento persistente fuera del repositorio. |
| `UVICORN_PORT` | Sobre-escribir puerto en scripts. | Respaldado por `uvicorn` si se usa `python backend/app.py`. |

---

## 3) GUI (WPF)

### 3.1 Requisitos
- Windows 10/11
- Visual Studio 2022 o superior
- .NET SDK 8.0
- Paquetes NuGet principales: `OpenCvSharp4`, `OpenCvSharp4.runtime.win`, `OpenCvSharp4.Extensions`, `CommunityToolkit.Mvvm`.

### 3.2 Archivos principales
- `MainWindow.xaml` / `.cs` — layout y binding principal.
- `BackendClient.cs` — cliente HTTP async para `/health`, `/fit_ok`, `/calibrate_ng`, `/infer`.
- `ROI/` — modelos/adorners (no modificar geometría base).
- `Overlays/` — sincronización canvas ↔ imagen (`RoiOverlay`).
- `Datasets/` — helpers para guardar PNG + JSON en `datasets/<role>/<roi>/<ok|ng>/`.

### 3.3 Configuración
`appsettings.json` ejemplo:
```json
{
  "Backend": {
    "BaseUrl": "http://127.0.0.1:8000",
    "DatasetRoot": "C:\\data\\brakedisc\\datasets"
  }
}
```

Puedes sobrescribir `BaseUrl` desde variables de entorno:

```powershell
$env:BRAKEDISC_BACKEND_BASEURL="http://192.168.1.20:8000"
```

o definir `BRAKEDISC_BACKEND_HOST` + `BRAKEDISC_BACKEND_PORT` para construir la URL automáticamente.

### 3.4 Flujo recomendado
1. Cargar imagen y dibujar ROI (respetando mínimo 10×10 px).
2. Exportar ROI canónico desde la pestaña **Dataset** (`Add OK/NG from current ROI`).
3. Entrenar con **Train memory (fit_ok)**.
4. Calibrar (opcional) con **Calibrate threshold**.
5. Ejecutar **Infer current ROI** para obtener `score`, `threshold`, heatmap y regiones.

### 3.5 Debugging GUI
- Usar `AppendLog` o `TraceSource` para registrar acciones y respuestas del backend.
- Validar que el heatmap se superpone correctamente (revisar tamaño del PNG vs. `Image.Source`).
- En caso de errores HTTP, mostrar mensaje amigable + detalle técnico en panel de logs.
## 4) Scripts auxiliares (`scripts/`)

| Script | Descripción |
|--------|-------------|
| `setup_dev.ps1` | Crea venv y ejecuta `pip install -r backend/requirements.txt`. |
| `run_backend.ps1` | Activa el venv y lanza `uvicorn backend.app:app`. |
| `run_gui.ps1` | Abre la solución WPF en Visual Studio. |
| `check_env.ps1` | Verifica dependencias básicas (Python, dotnet, git). |
> Ejecutar los scripts desde PowerShell con permisos adecuados (`Set-ExecutionPolicy -Scope Process RemoteSigned`).
## 5) Estándares de código
- Python: seguir PEP8, tipado opcional (`typing`) y logging estructurado (`logging.getLogger(__name__)`).
- C#: respetar convenciones .NET (PascalCase para métodos/clases, `_camelCase` para privados), usar `async/await` para I/O.
- Commits: mensajes en inglés (`feat:`, `fix:`, `docs:`, etc.).

---

## 6) Testing

### 6.1 Backend
- Tests unitarios en `backend/tests/` (FAISS opcional). Ejecutar con `pytest` si está disponible.
- Smoke tests manuales (`curl /fit_ok`, `/infer`, `/calibrate_ng`).

### 6.2 GUI
- Compilar solución (`Build > Build Solution`).
- Validar flujo dataset → fit → calibrate → infer con muestras de prueba.
- Verificar alineación ROI/heatmap tras redimensionar la ventana.
## 7) Roadmap sugerido

- [ ] Automatizar `pytest` y linters (GitHub Actions).
- [ ] Añadir modo batch para procesar múltiples ROIs.
- [ ] Persistir manifiestos por ROI con estado de entrenamiento/calibración.
- [ ] Integrar exportación de reportes (CSV/JSON) desde la GUI.
- [ ] Instrumentar métricas Prometheus desde FastAPI.
- [ ] Soportar múltiples ROIs simultáneos en la GUI sin romper adorners.
- [ ] Añadir modo *batch analyze* y exportación a CSV/DB.
- [ ] Investigar entrenamiento incremental / *online* para PatchCore.
- [ ] Documentar integración con Prometheus/Grafana.
## 8) Referencias cruzadas
- [ARCHITECTURE.md](ARCHITECTURE.md) — Visión de alto nivel y flujo de datos.
- [API_REFERENCE.md](API_REFERENCE.md) — Contratos HTTP detallados.
- [DATA_FORMATS.md](DATA_FORMATS.md) — Esquemas de requests/responses y archivos persistidos.
- [DEPLOYMENT.md](DEPLOYMENT.md) — Guía de despliegue local/prod.
- [LOGGING.md](LOGGING.md) — Política de logging y correlación GUI↔backend.
- [docs/mcp/overview.md](docs/mcp/overview.md) — Maintenance & Communication Plan.
