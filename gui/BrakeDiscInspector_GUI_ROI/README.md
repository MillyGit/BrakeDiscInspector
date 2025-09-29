# BrakeDiscInspector GUI — ROI Workflow

## 1. Objetivo

Aplicación WPF (.NET 8) que permite preparar ROIs, gestionar datasets y comunicarse con el backend FastAPI (PatchCore + DINOv2). La GUI exporta ROIs canónicos (crop + rotación) y consume los endpoints `/fit_ok`, `/calibrate_ng`, `/infer`.

---

## Índice rápido

- [Objetivo](#1-objetivo)
- [Flujo principal](#2-flujo-principal)
- [Estructura del proyecto](#3-estructura-del-proyecto)
- [Configuración](#4-configuración)
- [Problemas comunes](#5-problemas-comunes)
- [Registro](#6-registro)
- [Referencias](#7-referencias)

---

## 2. Flujo principal

1. Cargar imagen (BMP/PNG/JPG) en la vista principal.
2. Dibujar y rotar el ROI con los adorners existentes (`RoiAdorner`, `RoiRotateAdorner`).
3. **Dataset tab**:
   - `Add OK/NG from current ROI` → guarda PNG + metadata JSON en `datasets/<role>/<roi>/<ok|ng>/`.
   - `Remove selected`, `Open folder` para mantenimiento rápido.
4. **Train tab**:
   - `Train memory (fit_ok)` → empaqueta todos los PNG OK y llama a `/fit_ok`.
   - Muestra `n_embeddings`, `coreset_size`, `token_shape` y guarda el log.
5. **Calibrate tab** (opcional):
   - Llama a `/calibrate_ng` con scores OK/NG para fijar `threshold` y `area_mm2_thr`.
6. **Infer tab**:
   - `Infer current ROI` → llama a `/infer`, decodifica `heatmap_png_base64` y lo superpone.
   - Permite ajustar visualmente el umbral sin modificar el backend (slider local).

---

## 3. Estructura del proyecto

- `App.xaml` / `App.xaml.cs`
- `MainWindow.xaml` / `.cs` (tabs Dataset/Train/Calibrate/Infer)
- `Workflow/BackendClient.cs` (HttpClient async)
- `ROI/` — modelos y adorners (no modificar geometría base)
- `Overlays/` — sincronización canvas ↔ imagen (`RoiOverlay`)
- `Workflow/DatasetManager.cs` — helpers para PNG + JSON
- `ViewModels/` — lógica MVVM para cada tab

---

## 4. Configuración

`appsettings.json`:
```json
{
  "Backend": {
    "BaseUrl": "http://127.0.0.1:8000",
    "DatasetRoot": "C:\\data\\brakedisc\\datasets"
  }
}
```

También puedes sobreescribir `BaseUrl` mediante variables de entorno:

- `BRAKEDISC_BACKEND_BASEURL` / `BRAKEDISC_BACKEND_BASE_URL`
- o `BRAKEDISC_BACKEND_HOST` + `BRAKEDISC_BACKEND_PORT` (acepta también `HOST` / `PORT`)

La GUI normaliza automáticamente la URL (añade `http://` si falta) antes de usarla.

---

## 5. Problemas comunes

- **Adorners desalineados**: ejecutar `SyncOverlayToImage()` al cargar imagen y en `SizeChanged`.
- **Heatmap invertido**: verificar que el PNG devuelto por `/infer` se muestra con el mismo tamaño del ROI canónico.
- **Timeouts**: aumentar `HttpClient.Timeout` cuando se suben docenas de muestras a `/fit_ok`.
- **Memoria no encontrada**: ejecutar `/fit_ok` antes de `/infer` y revisar carpeta `backend/models/<role>/<roi>/`.

---

## 6. Registro

Utiliza `AppendLog` (o equivalente) para escribir en `logs/gui.log`:
```
2024-06-05 14:23:10.512 [INFO] FitOk role=Master1 roi=Pattern images=48 nEmb=34992 coreset=700
2024-06-05 14:23:35.901 [INFO] Infer role=Master1 roi=Pattern score=18.7 thr=20.0 regions=1 dt=142ms
```

---

## 7. Referencias

- [README.md](../../README.md) — visión general del proyecto
- [ARCHITECTURE.md](../../ARCHITECTURE.md) — flujo GUI ↔ backend
- [API_REFERENCE.md](../../API_REFERENCE.md) — contratos FastAPI
- [instructions_codex_gui_workflow.md](../../instructions_codex_gui_workflow.md) — guía detallada para agentes/colaboradores GUI
