
# LOGGING — BrakeDiscInspector

Guía de **logging y trazabilidad** para el backend (Flask) y la GUI (WPF). El objetivo es facilitar el diagnóstico, auditoría y mejora del rendimiento sin almacenar datos sensibles innecesarios.

---

## 1) Objetivos y principios

- **Trazabilidad** de acciones clave: carga de modelo, llamadas a endpoints, inferencia, errores.
- **Observabilidad**: latencias, tamaños, resultados agregados.
- **Privacidad**: evitar PII y rutas locales sensibles en los logs.
- **Mantenibilidad**: formato consistente, niveles claros, rotación periódica.

---

## 2) Backend (Flask)

### 2.1 Ubicación y formato
- Archivo: `backend/logs/backend.log`
- Configuración básica (en `app.py`):
  ```python
  logging.basicConfig(
      filename=os.path.join(LOG_DIR, "backend.log"),
      level=logging.INFO,
      format="%(asctime)s %(levelname)s %(message)s",
  )
  logger = logging.getLogger("BrakeDiscInspectorBackend")
  ```

### 2.2 Niveles recomendados
- `DEBUG`: datos de depuración (desactivado en prod; puede incluir matrices/shape).
- `INFO`: eventos normales (inicio app, carga modelo, requests, decisión OK/NG).
- `WARNING`: condiciones anómalas recuperables (threshold por defecto, annulus inválido).
- `ERROR`: fallos en inferencia o E/S.
- `CRITICAL`: indisponibilidad total del servicio.

### 2.3 Eventos mínimos a registrar
- **Inicio**: versión de lib, presencia de modelo, umbral cargado.
- **/analyze**:
  - Inicio/fin con **correlation id** (GUID) y latencia total.
  - Tamaño del PNG recibido en bytes.
  - Parámetros opcionales (mask/annulus presentes o no).
  - Resultado: `label`, `score`, `threshold` (redondeado).
- **/train_status**: respuesta enriquecida con `state`, `threshold`, `artifacts.*`, `model_runtime` y `log_tail`.
- **/match_master** (alias `/match_one`): estado (`stage`), `found`, `confidence`, `tm_best/tm_thr` y métricas del detector.
- **Errores**: traza con `logger.exception(...)`.

Ejemplo (pseudocódigo):
```python
rid = uuid.uuid4().hex[:8]
t0 = time.perf_counter()
logger.info(f"[{rid}] /analyze start bytes={len(data)} mask={'mask' in request.files} ann={'annulus' in request.form}")
# ... inferencia ...
dt = (time.perf_counter() - t0)*1000
logger.info(f"[{rid}] /analyze end label={label} score={score:.3f} thr={_threshold:.3f} dt_ms={dt:.1f}")
```

### 2.4 Rotación de logs
- En Windows: usar **Logman** o tareas programadas para rotar por tamaño/fecha.
- En Linux: `logrotate` (ejemplo `/etc/logrotate.d/brakedisc`):
  ```
  /opt/brakedisc/backend/logs/backend.log {
      weekly
      rotate 8
      compress
      missingok
      notifempty
      copytruncate
  }
  ```

### 2.5 Métricas opcionales
- Promedio y p95 de latencias `/analyze`.
- Conteo de `OK` vs `NG` por ventana temporal.
- Errores por tipo (decoding, model, annulus).

> Para setups avanzados, exportar a **Prometheus**/**Grafana** o enviar a **ELK**/**OpenSearch** vía Filebeat/Fluentd.

---

## 3) GUI (WPF)

### 3.1 Recomendación de implementación
- Usar `System.Diagnostics.TraceSource` o un simple `StreamWriter` con lock.
- Archivo sugerido: `gui/BrakeDiscInspector_GUI_ROI/logs/gui.log` (añadir a `.gitignore`).
- Nivel configurable por `appsettings` (opcional).

### 3.2 Eventos mínimos
- Inicio de la app y versión (OS, .NET).
- Carga de imagen: ruta **anonimizada** (solo nombre de archivo).
- ROI: cambios relevantes (tamaño, posición, `AngleDeg`), respetando rate‑limit de logs.
- Rotaciones del ROI (thumb NE del **RoiAdorner**).
- Click **Analyze**: dimensiones del crop, tamaño PNG enviado.
- Respuesta: `label`, `score`, `threshold` (no guardar imagen).
- Errores de red/HTTP y conversiones Mat↔BitmapSource.

Ejemplo (C#):
```csharp
void AppendLog(string msg) {
    var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {msg}";
    File.AppendAllText("logs/gui.log", line + Environment.NewLine);
}
```

### 3.3 Buenas prácticas
- No loggear rutas completas del usuario ni claves de API.
- No serializar bitmaps/bytes grandes en el log.
- Limitar la frecuencia de logs de movimiento/rotación.

---

## 4) Correlación GUI ↔ Backend

- Generar en la GUI un **CorrelationId** (GUID corto) por cada **Analyze** y adjuntarlo como header:
  - Header: `X-Correlation-Id: <id>`
- Backend: registrar el mismo header si existe.
- Beneficio: seguimiento de punta a punta (GUI→backend→respuesta).

Ejemplo GUI (C#):
```csharp
var id = Guid.NewGuid().ToString("N").Substring(0,8);
var req = new HttpRequestMessage(HttpMethod.Post, BackendAPI.AnalyzeEndpoint);
req.Headers.Add("X-Correlation-Id", id);
```
(Integrarlo en `BackendAPI.AnalyzeAsync` si se requiere).

---

## 5) Entornos (niveles y verbosidad)

- **Dev**: `DEBUG` + trazas detalladas; rotación menor.
- **Staging**: `INFO` + métricas; rotación semanal.
- **Prod**: `INFO/WARN/ERROR`; rotación semanal con compresión.

---

## 6) Seguridad y cumplimiento

- Evitar PII (nombres de usuario, emails, rutas personales).
- Evitar volcado de contenido binario/base64 de imágenes en logs.
- Si se guardan ejemplos para auditoría, hacerlo en carpetas separadas y con **consentimiento**/política de retención.
- Revisar permisos de archivos de log (solo lectura/escritura para el servicio).

---

## 7) Checklist

- [ ] `backend/logs/` existe y es escribible.
- [ ] `logging.basicConfig(...)` inicializa nivel y formato.
- [ ] Se registra inicio de app, umbral y estado del modelo.
- [ ] `/analyze` loggea bytes recibidos, decisión y latencia.
- [ ] GUI registra acciones principales sin PII.
- [ ] Rotación de logs definida (logrotate/NSSM/Task Scheduler).
- [ ] (Opcional) CorrelationId de extremo a extremo.
