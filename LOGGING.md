# 📌 Actualización — 2025-10-07

**Cambios clave documentados en esta versión:**
- Se detalla cómo instrumentar `app.py` e `InferenceEngine` con logs estructurados para `/fit_ok`, `/calibrate_ng`, `/infer`.
- Se alinean los destinos de log de la GUI con los archivos creados durante dataset/inferencia (`roi_analyze_master.log`, etc.).
- Se incluyen recomendaciones de correlación (`X-Correlation-Id`) y rotación en despliegues Windows/Linux.

# LOGGING — BrakeDiscInspector

Política de logging y trazabilidad para el backend FastAPI (PatchCore + DINOv2) y la GUI WPF.

---

## Índice rápido

- [Principios](#1-principios)
- [Backend (FastAPI)](#2-backend-fastapi)
- [GUI (WPF)](#3-gui-wpf)
- [Seguridad](#4-seguridad)
- [Checklist](#5-checklist)

---

## 1) Principios

- **Observabilidad**: registrar inicio, parámetros y tiempos de cada operación crítica.
- **Correlación**: propagar un identificador (`X-Correlation-Id`) desde la GUI hacia el backend para unir eventos.
- **Retención**: definir rotación y purgado acorde al entorno (laboratorio vs producción).

---

## 2) Backend (FastAPI)

### 2.1 Configuración
- El módulo raíz (`app.py`) obtiene un `logger` con `logging.getLogger(__name__)` y crea registros al arrancar mediante `logging.basicConfig` cuando se ejecuta como script.【F:backend/app.py†L1-L214】
- Al ejecutar con Uvicorn/Gunicorn usar `--log-level info` y, si se necesita más detalle, habilitar `uvicorn.error` y `uvicorn.access` en un fichero aparte.

### 2.2 Eventos recomendados

| Evento | Campos sugeridos |
|--------|------------------|
| Inicio | versión (`0.1.0`), dispositivo (`cpu`/`cuda`), `MODELS_DIR` activo. |
| `/fit_ok` | `role_id`, `roi_id`, nº imágenes, bytes totales, `n_embeddings`, `coreset_size`, `token_shape`, `coreset_rate`. |
| `/calibrate_ng` | `role_id`, `roi_id`, len arrays OK/NG, `threshold`, `area_mm2_thr`, `score_percentile`. |
| `/infer` | `role_id`, `roi_id`, tamaño imagen, `shape` presente, `score`, `threshold`, nº regiones, tiempo total. |
| Errores | Mensaje y `traceback` completo (`logger.exception`). |

Pseudocódigo:
```python
cid = headers.get("X-Correlation-Id") or uuid.uuid4().hex[:8]
log.info("[%s] /infer start role=%s roi=%s bytes=%d shape=%s", cid, role_id, roi_id, len(raw), bool(shape))
try:
    result = engine.run(...)
    log.info("[%s] /infer end score=%.2f thr=%s regions=%d", cid, result["score"], result.get("threshold"), len(result["regions"]))
except Exception:
    log.exception("[%s] /infer failed", cid)
    raise
```

### 2.3 Rotación y destino
- **Linux**: configurar `/var/log/brakedisc/backend.log` con `logrotate` semanal (7 versiones comprimidas).
- **Windows/NSSM**: redirigir `stdout`/`stderr` a un fichero (`backend\logs\backend.log`) y rotarlo mediante Task Scheduler o NSSM `AppRotateFiles`.

### 2.4 Métricas opcionales
- Latencias p95 de `/infer` y `/fit_ok` (exportables vía `prometheus_fastapi_instrumentator`).
- Conteo de inferencias por `(role_id, roi_id)` para seguimiento de uso.

---

## 3) GUI (WPF)

### 3.1 Destinos de log
- Registrar eventos en `%LOCALAPPDATA%/BrakeDiscInspector/logs/` (sugerido):
  - `roi_analyze_master.log` → exportación de ROIs y generación de dataset.
  - `roi_load_coords.log` → carga/guardado de layouts y presets.
  - `gui_heatmap.log` → resultados de `/fit_ok`, `/calibrate_ng`, `/infer` (score, threshold, nº regiones).
- Utilizar `StreamWriter` asíncrono o `Serilog` si ya está integrado en la solución.

### 3.2 Eventos mínimos

| Evento | Contenido |
|--------|-----------|
| Inicio de app | Versión GUI, `DatasetRoot`, `Backend.BaseUrl`. |
| Carga imagen | Ruta (anonimizada) y dimensiones originales. |
| Exportar ROI | `role_id`, `roi_id`, `shape`, tamaño PNG, `mm_per_px`, ángulo. |
| `/fit_ok` | Nº imágenes enviadas, `n_embeddings`, `coreset_size`, duración. |
| `/calibrate_ng` | Arrays OK/NG usados, `threshold` devuelto. |
| `/infer` | `score`, `threshold`, nº regiones, duración, path del heatmap temporal. |
| Errores | Mensaje amigable + detalle técnico (`HttpRequestException`, validaciones). |

### 3.3 Correlación GUI ↔ backend
- Generar `X-Correlation-Id` por operación (`Guid.NewGuid().ToString("N").Substring(0,8)`) y adjuntarlo en `BackendClient` vía `HttpRequestMessage.Headers` antes de cada POST/GET.
- Registrar el ID en los logs GUI y backend para facilitar el trazado cuando se analicen incidencias.

### 3.4 Supervisión visual
- Al recibir `heatmap_png_base64`, guardar temporalmente el PNG con el mismo `CorrelationId` para comparar con los logs.
- Almacenar respuestas completas (JSON) cuando se depuren umbrales o regresiones.

---

## 4) Seguridad

- No registrar rutas completas ni IDs sensibles; anonimizar nombres de archivo cuando sea posible.
- Revisar periódicamente los logs para asegurarse de que no contienen imágenes o datos confidenciales.
- Establecer permisos restringidos (`chmod 750` en Linux, ACL específica en Windows) sobre carpetas de logs.

---

## 5) Checklist

- [ ] Backend registra inicio, `/fit_ok`, `/calibrate_ng`, `/infer` con tiempos y correlation id.
- [ ] La GUI escribe logs en disco y muestra los últimos eventos al usuario.
- [ ] La rotación de logs está configurada en el entorno objetivo (logrotate, Task Scheduler, etc.).
- [ ] Se documenta en `docs/mcp/latest_updates.md` cualquier cambio en política de logging.
- [ ] Métricas opcionales (si se habilitan) quedan enlazadas desde el MCP.

Para coordinación adicional consulta [docs/mcp/overview.md](docs/mcp/overview.md).
