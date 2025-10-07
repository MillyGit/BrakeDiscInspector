
# 📌 Actualización — 2025-10-07

**Cambios clave (GUI):**
- Corrección de salto del frame al clicar adorner (círculo/annulus): cálculo y propagación del centro reales en `SyncModelFromShape` y sincronización `X,Y = CX,CY` en `CreateLayoutShape`.
- Bbox SIEMPRE cuadrado para circle/annulus; overlay heatmap alineado.
- Decisiones del proyecto y parámetros vigentes documentados.

**Cambios clave (Backend):**
- PatchCore + DINOv2 ViT-S/14; endpoints `/health`, `/fit_ok`, `/calibrate_ng`, `/infer`; persistencia por `(role_id, roi_id)`.


# BrakeDiscInspector

**BrakeDiscInspector** es una solución integral para inspección de discos de freno que combina una **GUI WPF** para preparar imágenes y un **backend FastAPI** que implementa detección de anomalías basada en **PatchCore + DINOv2**.

La documentación está organizada para que cualquier colaborador (humano o agente) pueda localizar rápidamente los contratos, las guías de desarrollo y los flujos operativos.

> **Ruta de lectura sugerida**
> 1. Empezar con la [Arquitectura](#-estructura-del-proyecto) para entender los componentes.
> 2. Revisar la [guía de desarrollo](DEV_GUIDE.md) y el [flujo GUI](instructions_codex_gui_workflow.md) según tu perfil.
> 3. Consultar las fichas de API y formatos cuando integres el backend.

## 🧭 Índice rápido

- [Características principales](#-características-principales)
- [Estructura del proyecto](#-estructura-del-proyecto)
- [Puesta en marcha rápida](#-puesta-en-marcha-rápida)
- [API principal](#-api-principal)
- [ROI y shapes](#-roi-y-shapes)
- [Documentación relacionada](#-documentación-relacionada)
- [Checklist para Codex](#-checklist-para-codex)

---

## ✨ Características principales

- **Pipeline “good-only”**: el backend aprende únicamente a partir de muestras OK gracias a un extractor DINOv2 ViT-S/14 congelado y memoria PatchCore con coreset k-center greedy.【F:backend/app.py†L40-L118】【F:backend/patchcore.py†L1-L200】
- **Flujo completo en la GUI**: gestión de datasets por `(role_id, roi_id)`, entrenamiento (`/fit_ok`), calibración (`/calibrate_ng`) e inferencia (`/infer`) usando el ROI canónico exportado desde los adorners existentes.【F:instructions_codex_gui_workflow.md†L1-L120】
- **Heatmaps y contornos**: el backend devuelve mapas de calor en PNG Base64 y regiones con área en px/mm² listos para superponer en la GUI.【F:backend/app.py†L118-L199】
- **Persistencia por rol/ROI**: embeddings, índices e información de calibración se almacenan en `models/<role>/<roi>/` para reutilizar entrenamientos previos.【F:backend/storage.py†L1-L200】
- **Documentación extensa**: guías de arquitectura, datos, despliegue, logging y MCP sincronizadas con la implementación actual.

---

## 📂 Estructura del proyecto

```
BrakeDiscInspector/
├─ backend/
│  ├─ app.py                 # FastAPI con /health, /fit_ok, /calibrate_ng, /infer
│  ├─ features.py            # Extractor DINOv2 (timm)
│  ├─ patchcore.py           # Memoria PatchCore y kNN (FAISS/sklearn)
│  ├─ infer.py               # Posprocesado: heatmap, score, contornos
│  ├─ calib.py               # Selección de umbral con 0–3 NG
│  ├─ roi_mask.py            # Construcción de máscaras rect/círculo/annulus
│  ├─ storage.py             # Persistencia en models/<role>/<roi>/
│  ├─ requirements.txt       # Dependencias (torch, timm, fastapi, faiss, etc.)
│  └─ README_backend.md      # Guía específica del servicio
├─ gui/
│  └─ BrakeDiscInspector_GUI_ROI/
│     ├─ App.xaml / App.xaml.cs
│     ├─ MainWindow.xaml / MainWindow.xaml.cs
│     ├─ Workflow/BackendClient.cs (cliente HTTP async)
│     ├─ ROI/                # Modelos y adorners existentes (no modificar)
│     ├─ Overlays/           # Sincronización imagen ↔ canvas
│     └─ Workflow/DatasetManager.cs  # Gestión de muestras OK/NG y metadatos
├─ docs/
│  └─ mcp/                   # Maintenance & Communication Plan (MCP)
├─ scripts/                  # Utilidades (PowerShell) para entorno Windows
├─ README.md                 # Este archivo
├─ ARCHITECTURE.md           # Arquitectura actualizada
├─ API_REFERENCE.md          # Contratos FastAPI
├─ DATA_FORMATS.md           # Esquemas de requests/responses
├─ DEV_GUIDE.md              # Preparación de entorno y flujos de trabajo
├─ DEPLOYMENT.md             # Despliegue local, laboratorio y producción
├─ LOGGING.md                # Política de logging y observabilidad
├─ CONTRIBUTING.md           # Normas de contribución
└─ agents.md                 # Playbook para agentes/IA colaboradores
```

---

## 🚀 Puesta en marcha rápida

### Backend (Python / FastAPI)

1. Crear entorno virtual e instalar dependencias:
   ```bash
   cd backend
   python -m venv .venv
   source .venv/bin/activate      # PowerShell: .venv\Scripts\Activate.ps1
   pip install -r requirements.txt
   ```
2. Lanzar el servicio en desarrollo:
   ```bash
   uvicorn backend.app:app --reload --port 8000
   ```
3. Verificar estado:
   ```bash
   curl http://127.0.0.1:8000/health
   ```
4. Entrenar la memoria con muestras OK (ejemplo):
   ```bash
   curl -X POST http://127.0.0.1:8000/fit_ok \
        -F role_id=Master1 \
        -F roi_id=Pattern \
        -F mm_per_px=0.20 \
        -F images=@datasets/Master1/Pattern/ok/sample_001.png
   ```

Los artefactos se guardarán automáticamente en `backend/models/Master1/Pattern/`.

### GUI (WPF / .NET 8)

1. Abrir `gui/BrakeDiscInspector_GUI_ROI.sln` en Visual Studio 2022 o superior.
2. Restaurar paquetes NuGet (`OpenCvSharp4`, `OpenCvSharp4.runtime.win`, `OpenCvSharp4.Extensions`, `CommunityToolkit.Mvvm`).
3. Configurar `appsettings.json`:
   ```json
   {
     "Backend": {
       "BaseUrl": "http://127.0.0.1:8000",
       "DatasetRoot": "C:\\data\\brakedisc\\datasets"
     }
   }
   ```
4. Flujo recomendado:
   1. Dibujar y rotar el ROI con los adorners existentes.
   2. Guardar muestras OK/NG desde el panel **Dataset** (la GUI exporta PNG + metadata JSON).
   3. Ejecutar **Train memory (fit_ok)** y revisar `n_embeddings`, `coreset_size` y `token_shape`.
   4. (Opcional) Ejecutar **Calibrate threshold** aportando scores OK/NG.
   5. Lanzar **Infer current ROI** para obtener `score`, `threshold` y heatmap superpuesto.

### Variables y rutas clave

| Componente | Variable/Ruta | Descripción |
|------------|---------------|-------------|
| Backend | `MODELS_DIR` | Cambia la carpeta donde se guardan `memory.npz`, `index.faiss` y `calib.json`. |
| Backend | `CORESET_RATE`, `INPUT_SIZE`, `DEVICE` | Ajustan hiperparámetros de PatchCore y del extractor DINOv2. |
| GUI | `appsettings.json:Backend.BaseUrl` | URL del servicio FastAPI (se puede sobrescribir con `BRAKEDISC_BACKEND_BASEURL`). |
| GUI | `appsettings.json:Backend.DatasetRoot` | Carpeta donde la GUI guarda `datasets/<role>/<roi>/<ok|ng>/`. |
| Compartido | `datasets/<role>/<roi>/manifest.json` | (Opcional) Estado del dataset y del entrenamiento por ROI. |

---

## 🔗 API principal

- `GET /health` → estado del servicio, dispositivo y versión del modelo base.
- `POST /fit_ok` → recibe lotes de ROI OK, construye memoria PatchCore y persiste embeddings/índices.
- `POST /calibrate_ng` → calcula umbral por `(role_id, roi_id)` a partir de scores OK/NG y guarda `calib.json`.
- `POST /infer` → infiere sobre un ROI canónico y devuelve `score`, `threshold`, `heatmap_png_base64`, `regions` y `token_shape`.

Detalles ampliados y ejemplos en [API_REFERENCE.md](API_REFERENCE.md) y [DATA_FORMATS.md](DATA_FORMATS.md).

---

## 📐 ROI y shapes

- Los ROIs se dibujan en la GUI con adorners existentes (`RoiAdorner`, `RoiRotateAdorner`, `RoiOverlay`).
- El backend siempre recibe **ROI canónico** (crop + rotación ya aplicados) y, opcionalmente, una máscara `shape` en coordenadas del ROI (`rect`, `circle`, `annulus`).
- La GUI mantiene letterboxing sincronizado para que los heatmaps retornados encajen de forma exacta sobre la imagen original.

Más detalles prácticos en [ROI_AND_MATCHING_SPEC.md](ROI_AND_MATCHING_SPEC.md).

---

## 📚 Documentación relacionada

- **Arquitectura y contratos**
  - [ARCHITECTURE.md](ARCHITECTURE.md) — componentes, diagrama de flujo y reglas de coordinación GUI↔backend.
  - [ROI_AND_MATCHING_SPEC.md](ROI_AND_MATCHING_SPEC.md) — geometría detallada, shapes y conversiones.
  - [API_REFERENCE.md](API_REFERENCE.md) — endpoints HTTP con ejemplos `curl`.
  - [DATA_FORMATS.md](DATA_FORMATS.md) — esquemas JSON, PNG y artefactos persistidos.
- **Operación y desarrollo**
  - [DEV_GUIDE.md](DEV_GUIDE.md) — setup, scripts y estándares de código.
  - [DEPLOYMENT.md](DEPLOYMENT.md) — despliegue local/LAN/producción y smoke tests.
  - [LOGGING.md](LOGGING.md) — política de logging y correlación de eventos.
  - [backend/README_backend.md](backend/README_backend.md) — referencia operativa del servicio FastAPI.
- **Coordinación y agentes**
  - [instructions_codex_gui_workflow.md](instructions_codex_gui_workflow.md) — checklist completo para desarrollar la GUI.
  - [backend/agents_for_backend.md](backend/agents_for_backend.md) — playbook de mantenimiento del backend.
  - [docs/mcp/overview.md](docs/mcp/overview.md) — Maintenance & Communication Plan y responsables.
  - [docs/mcp/latest_updates.md](docs/mcp/latest_updates.md) — bitácora de cambios coordinados.

---

¿Quieres contribuir? Revisa [CONTRIBUTING.md](CONTRIBUTING.md) y participa 🚀

## 📑 Documentación adicional

- **[ARCHITECTURE.md](ARCHITECTURE.md)** → flujo GUI/Backend, diagrama mermaid.
- **[API_REFERENCE.md](API_REFERENCE.md)** → endpoints, contratos, ejemplos curl.
- **[ROI_AND_MATCHING_SPEC.md](ROI_AND_MATCHING_SPEC.md)** → definición ROI, rotación, annulus.
- **[DATA_FORMATS.md](DATA_FORMATS.md)** → formatos de requests/responses.
- **[DEV_GUIDE.md](DEV_GUIDE.md)** → setup local completo.
- **[DEPLOYMENT.md](DEPLOYMENT.md)** → despliegue local/prod y tests.
- **[LOGGING.md](LOGGING.md)** → niveles y rutas de logs.
- **[CONTRIBUTING.md](CONTRIBUTING.md)** → normas para contribuir.
- **[MCP Overview](docs/mcp/overview.md)** → responsables, cadencia de releases y registro de artefactos.
- **[MCP Latest Updates](docs/mcp/latest_updates.md)** → historial cronológico de decisiones MCP.

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
  - [ROI & Shapes](ROI_AND_MATCHING_SPEC.md)
- [Datos](DATA_FORMATS.md)
- [Dev Guide](DEV_GUIDE.md)
- [Despliegue](DEPLOYMENT.md)
- [Logging](LOGGING.md)
- [Contribución](CONTRIBUTING.md)


---
