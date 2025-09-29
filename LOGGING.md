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

- **Observabilidad**: registrar tiempos, tamaños y resultados clave sin exponer datos sensibles.
- **Correlación**: usar `X-Correlation-Id` para unir eventos GUI ↔ backend.
- **Retención**: rotar y conservar logs críticos según políticas de planta.

---

## 2) Backend (FastAPI)

### 2.1 Configuración básica

- El backend usa `logging.getLogger(__name__)` en módulos como `app.py`, `infer.py` y `storage.py`.
- Inicializa logging en `if __name__ == "__main__"` cuando se ejecuta con `uvicorn backend.app:app` (usar `--log-level info`).
- Para despliegues con Gunicorn, definir `log-config` o variables `LOG_LEVEL` según necesidad.
- Habilita `uvicorn.access` si necesitas auditoría HTTP; puede redirigirse a fichero separado usando la configuración de Gunicorn/Uvicorn.

### 2.2 Eventos mínimos

| Evento | Campos sugeridos |
|--------|------------------|
| Inicio del servicio | versión, dispositivo (`cpu`/`cuda`), `models_dir` activo. |
| `/fit_ok` | `role_id`, `roi_id`, nº de imágenes, bytes totales, `n_embeddings`, `coreset_size`, `token_shape`, tiempo total. |
| `/calibrate_ng` | `role_id`, `roi_id`, tamaño arrays OK/NG, `threshold`, `score_percentile`, `area_mm2_thr`. |
| `/infer` | `role_id`, `roi_id`, tamaño PNG, presencia de `shape`, `score`, `threshold`, nº regiones, latencia total. |
| Errores | Mensaje y `traceback` completo (`logger.exception`). |

Ejemplo (pseudocódigo):
```python
cid = headers.get("X-Correlation-Id", uuid.uuid4().hex[:8])
t0 = time.perf_counter()
log.info("[%s] /infer start role=%s roi=%s bytes=%d shape=%s", cid, role_id, roi_id, len(data), bool(shape))
...
dt = (time.perf_counter() - t0) * 1000
log.info("[%s] /infer end score=%.2f thr=%s regions=%d dt_ms=%.1f", cid, out["score"], out.get("threshold"), len(out["regions"]), dt)
```

### 2.3 Rotación

- **Linux**: usar `logrotate` (ejemplo `/etc/logrotate.d/brakedisc` con rotación semanal + compresión).
- **Windows**: emplear Task Scheduler o NSSM para rotar ficheros en `backend/logs/`.
- Registrar la ruta exacta en documentación de despliegue.

### 2.4 Métricas opcionales

- Promedio y percentil 95 de latencias `/infer`.
- Conteo de `OK` vs `NG` por rol/ROI.
- Uso opcional de Prometheus (`prometheus_fastapi_instrumentator`) para exponer métricas.

---

## 3) GUI (WPF)

### 3.1 Recomendaciones

- Centralizar logs en `gui/logs/gui.log` (añadir a `.gitignore`).
- Usar un `ConcurrentQueue` o `ObservableCollection` para mostrar eventos en pantalla y escribirlos en disco.
- Formato sugerido: `yyyy-MM-dd HH:mm:ss.fff [Nivel] Mensaje`.

### 3.2 Eventos mínimos

| Evento | Contenido |
|--------|-----------|
| Inicio de la app | Versión, ruta del dataset, backend configurado. |
| Carga de imagen | Nombre de archivo (anonimizado) y dimensiones. |
| ROI exportada | `role_id`, `roi_id`, tamaño PNG generado, ángulo, `shape`. |
| `/fit_ok` | Tiempo de ejecución y resumen de respuesta (`n_embeddings`, `coreset_size`). |
| `/calibrate_ng` | `threshold` devuelto, tamaños de arrays. |
| `/infer` | `score`, `threshold`, nº regiones, latencia. |
| Errores | Mensaje y detalles (`HttpRequestException`, validaciones). |

### 3.3 Correlación con backend

- Generar `X-Correlation-Id` por cada operación (ej. `Guid.NewGuid().ToString("N").Substring(0,8)`).
- Adjuntar el header en todas las llamadas HTTP y registrar el mismo ID en los logs GUI.

---

## 4) Seguridad

- No registrar rutas completas del usuario ni datos sensibles.
- Evitar almacenar imágenes o blobs en logs (solo tamaños/nombres anónimos).
- Asegurar permisos restrictivos en carpetas de log (`chmod 750` en Linux; ACLs en Windows).

---

## 5) Checklist

- [ ] Backend registra inicio, `/fit_ok`, `/calibrate_ng`, `/infer` y errores con correlation id.
- [ ] Rotación de logs configurada en el entorno objetivo.
- [ ] GUI guarda logs locales sin PII y muestra últimos eventos al usuario.
- [ ] `X-Correlation-Id` presente en ambas mitades (GUI ↔ backend).
- [ ] Métricas opcionales documentadas en `docs/mcp/latest_updates.md` si se habilitan.

Para coordinación entre equipos revisar [docs/mcp/overview.md](docs/mcp/overview.md).
