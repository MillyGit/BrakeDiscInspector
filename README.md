# 📌 Actualización — 2025-10-07

**Cambios clave documentados en esta versión:**
- GUI WPF consolidada con flujo completo Dataset → `/fit_ok` → `/calibrate_ng` → `/infer`, manteniendo adorners originales y sincronización de overlays.
- Backend FastAPI estabilizado sobre PatchCore + DINOv2 ViT-S/14 con persistencia por `(role_id, roi_id)` y contratos alineados a `app.py`/`infer.py`.
- Documentación revisada para reflejar almacenamiento `datasets/<role>/<roi>/<ok|ng>/` y artefactos `backend/models/<role>/<roi>/`.

# BrakeDiscInspector

**BrakeDiscInspector** combina una **GUI WPF (.NET 8)** para preparar y analizar Regiones de Interés (ROI) con un **backend FastAPI (Python 3.10+)** que implementa detección de anomalías *good-only* mediante **PatchCore** y un extractor **DINOv2 ViT-S/14** congelado.

La documentación está pensada para que cualquier colaborador pueda retomar el proyecto tras pérdida de contexto: explica los flujos principales, cómo levantar los componentes y qué artefactos se generan.

> **Ruta de lectura sugerida**
> 1. Revisa la [arquitectura](#-estructura-del-proyecto) para ubicar cada módulo.
> 2. Sigue la [guía de desarrollo](DEV_GUIDE.md) según tu perfil (backend o GUI).
> 3. Consulta los contratos y formatos en [API_REFERENCE.md](API_REFERENCE.md) y [DATA_FORMATS.md](DATA_FORMATS.md).

## 🧭 Índice rápido

- [Características principales](#-características-principales)
- [Estructura del proyecto](#-estructura-del-proyecto)
- [Puesta en marcha rápida](#-puesta-en-marcha-rápida)
- [API principal](#-api-principal)
- [ROI y shapes](#-roi-y-shapes)
- [Documentación relacionada](#-documentación-relacionada)

---

## ✨ Características principales

- **Pipeline good-only**: el backend extrae embeddings con `DinoV2Features` (`vit_small_patch14_dinov2.lvd142m`) y construye memoria PatchCore con coreset k-center greedy antes de guardar `memory.npz` por `(role_id, roi_id)`.【F:backend/app.py†L40-L118】【F:backend/storage.py†L1-L64】
- **Inferencia con heatmaps**: `InferenceEngine.run` genera mapas de calor, calcula percentiles (p99 por defecto) y devuelve regiones filtradas por área en mm² junto con el PNG base64 listo para superponer en la GUI.【F:backend/infer.py†L17-L132】【F:backend/infer.py†L136-L181】
- **GUI orquestada**: `MainWindow.xaml.cs` delega en `Workflow/BackendClient.cs` para llamar a `/fit_ok`, `/calibrate_ng` e `/infer`, mientras `Workflow/DatasetManager.cs` persiste muestras en `datasets/<role>/<roi>/<ok|ng>/` con metadatos JSON (`shape_json`, `mm_per_px`, ángulo, timestamp).【F:gui/BrakeDiscInspector_GUI_ROI/MainWindow.xaml.cs†L1-L160】【F:gui/BrakeDiscInspector_GUI_ROI/Workflow/BackendClient.cs†L20-L173】【F:gui/BrakeDiscInspector_GUI_ROI/Workflow/DatasetManager.cs†L18-L80】
- **Sincronización ROI↔heatmap**: el pipeline de exportación reutiliza los adorners existentes (`RoiAdorner`, `RoiRotateAdorner`, `RoiOverlay`) y mantiene coherencia con la máscara enviada al backend (`shape` JSON).【F:gui/BrakeDiscInspector_GUI_ROI/RoiAdorner.cs†L1-L200】【F:backend/roi_mask.py†L1-L160】
- **Documentación operativa**: guías para despliegue, logging y MCP alineadas con el estado del código a octubre de 2025.

---

## 📂 Estructura del proyecto

```
BrakeDiscInspector/
├─ backend/
│  ├─ app.py                 # FastAPI con /health, /fit_ok, /calibrate_ng, /infer
│  ├─ features.py            # Wrapper DINOv2 (timm) y normalización
│  ├─ patchcore.py           # Memoria PatchCore + coreset + kNN/FAISS
│  ├─ infer.py               # Heatmap, percentiles, contornos y regiones
│  ├─ calib.py               # Selección de threshold con percentiles OK/NG
│  ├─ storage.py             # Artefactos persistidos en models/<role>/<roi>/
│  └─ requirements.txt       # Dependencias (torch 2.x, timm, faiss, fastapi…)
├─ gui/
│  └─ BrakeDiscInspector_GUI_ROI/
│     ├─ MainWindow.xaml / .cs        # Layout principal y comandos
│     ├─ Workflow/BackendClient.cs    # Cliente HTTP async (fit/calibrate/infer)
│     ├─ Workflow/DatasetManager.cs   # Exportación PNG + JSON de ROIs canónicos
│     ├─ ROI/*.cs                     # Modelos y adorners (no modificar geometría)
│     └─ RoiOverlay.cs                # Sincronización imagen ↔ canvas (Stretch=Uniform)
├─ docs/mcp/                # Maintenance & Communication Plan
├─ scripts/                 # Utilidades PowerShell para entorno Windows
├─ *.md                     # Guías actualizadas (README, API, datos, despliegue…)
└─ agents.md                # Playbook y restricciones para contribuciones asistidas
```

---

## 🚀 Puesta en marcha rápida

### Backend (Python / FastAPI)

1. Crear entorno virtual e instalar dependencias:
   ```bash
   cd backend
   python -m venv .venv
   source .venv/bin/activate      # PowerShell: .venv\\Scripts\\Activate.ps1
   pip install -r requirements.txt
   ```
2. Lanzar el servicio en desarrollo:
   ```bash
   uvicorn backend.app:app --reload --host 127.0.0.1 --port 8000
   ```
3. Verificar estado y entrenar un rol de ejemplo:
   ```bash
   curl http://127.0.0.1:8000/health
   curl -X POST http://127.0.0.1:8000/fit_ok \
        -F role_id=Master1 \
        -F roi_id=Pattern \
        -F mm_per_px=0.20 \
        -F images=@datasets/Master1/Pattern/ok/sample_001.png
   ```

Los artefactos se guardan en `backend/models/Master1/Pattern/` (`memory.npz`, `index.faiss`, `calib.json`).

### GUI (WPF / .NET 8)

1. Abrir `gui/BrakeDiscInspector_GUI_ROI/BrakeDiscInspector_GUI_ROI.sln` en Visual Studio 2022+.
2. Restaurar paquetes NuGet (`OpenCvSharp4`, `OpenCvSharp4.runtime.win`, `CommunityToolkit.Mvvm`, etc.).
3. Ajustar `appsettings.json`:
   ```json
   {
     "Backend": {
       "BaseUrl": "http://127.0.0.1:8000",
       "DatasetRoot": "C:\\data\\brakedisc\\datasets",
       "MmPerPx": 0.20
     }
   }
   ```
4. Flujo recomendado:
   1. Dibujar la ROI (rect/círculo/annulus) y asegurar cobertura adecuada.
   2. Guardar muestras OK/NG desde la pestaña **Dataset** (genera PNG + metadata JSON con `shape_json`).
   3. Ejecutar **Train memory (`/fit_ok`)** y revisar `n_embeddings`, `coreset_size`, `token_shape`.
   4. (Opcional) Recolectar scores y lanzar **Calibrate (`/calibrate_ng`)**.
   5. Ejecutar **Infer current ROI** para obtener `score`, `threshold`, heatmap y `regions`.

### Variables y rutas clave

| Componente | Clave | Descripción |
|------------|-------|-------------|
| Backend | `MODELS_DIR` | Carpeta raíz donde se guardan memoria (`memory.npz`), índice (`index.faiss`) y calibración (`calib.json`). |
| Backend | `CORESET_RATE`, `INPUT_SIZE`, `DEVICE` | Hiperparámetros leídos en `app.py`/`features.py` para controlar el coreset y el dispositivo. |
| GUI | `Backend.BaseUrl` | URL del servicio FastAPI (se puede sobrescribir con variables `BRAKEDISC_BACKEND_*`). |
| GUI | `DatasetRoot` | Ruta donde `DatasetManager` crea `datasets/<role>/<roi>/<ok|ng>/`. |

---

## 🔗 API principal

- `GET /health` → Estado del servicio, dispositivo (`cpu`/`cuda`), modelo base y versión.
- `POST /fit_ok` → Construye memoria PatchCore a partir de PNG/JPG OK, devuelve `n_embeddings`, `coreset_size`, `token_shape`, ratios del coreset.
- `POST /calibrate_ng` → Persiste `calib.json` con `threshold`, percentiles OK/NG, `mm_per_px` y `area_mm2_thr`.
- `POST /infer` → Genera `score`, `threshold` (si calibrado), `heatmap_png_base64`, `regions` (bbox + áreas px/mm²) y `token_shape` para sincronizar overlays.

Más ejemplos y payloads detallados en [API_REFERENCE.md](API_REFERENCE.md) y [DATA_FORMATS.md](DATA_FORMATS.md).

---

## 📐 ROI y shapes

- La GUI exporta siempre ROIs **canónicos** (crop + rotación) reutilizando el pipeline compartido con “Save Master/Pattern”.
- El backend acepta una máscara opcional `shape` (`rect`, `circle`, `annulus`) expresada en píxeles del ROI canónico; la usa para enmascarar el heatmap antes de calcular el score y las regiones.【F:backend/roi_mask.py†L1-L160】
- `InferenceEngine` devuelve `regions` en coordenadas del ROI canónico; la GUI convierte áreas a mm² usando el `mm_per_px` suministrado.

Más detalles prácticos en [ROI_AND_MATCHING_SPEC.md](ROI_AND_MATCHING_SPEC.md).

---

## 📚 Documentación relacionada

- **Arquitectura y contratos**
  - [ARCHITECTURE.md](ARCHITECTURE.md) — Componentes, diagramas y flujo extremo a extremo.
  - [API_REFERENCE.md](API_REFERENCE.md) — Endpoints FastAPI con ejemplos `curl`.
  - [DATA_FORMATS.md](DATA_FORMATS.md) — Esquemas de requests, responses y artefactos.
  - [ROI_AND_MATCHING_SPEC.md](ROI_AND_MATCHING_SPEC.md) — Geometría ROI, máscaras y conversiones.
- **Operación y desarrollo**
  - [DEV_GUIDE.md](DEV_GUIDE.md) — Preparación de entorno, scripts, estándares de código.
  - [DEPLOYMENT.md](DEPLOYMENT.md) — Despliegue local, laboratorio y producción.
  - [LOGGING.md](LOGGING.md) — Política de logging y correlación GUI ↔ backend.
- **Coordinación y agentes**
  - [agents.md](agents.md) — Playbook con restricciones críticas (no alterar adorners ni contratos).
  - [docs/mcp/overview.md](docs/mcp/overview.md) — Maintenance & Communication Plan.
  - [docs/mcp/latest_updates.md](docs/mcp/latest_updates.md) — Registro cronológico de hitos.

¿Quieres contribuir? Consulta [CONTRIBUTING.md](CONTRIBUTING.md) y comparte tus mejoras 🚀
