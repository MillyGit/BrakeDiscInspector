# 📌 Actualización — 2025-10-07

**Cambios clave documentados en esta versión:**
- Se alinean los pasos de despliegue con los endpoints vigentes (`/health`, `/fit_ok`, `/calibrate_ng`, `/infer`) y la estructura `backend/models/<role>/<roi>/`.
- Se añaden recomendaciones específicas para variables `DEVICE`, `MODELS_DIR`, `CORESET_RATE` y despliegue NSSM/systemd.
- Se amplía la sección de troubleshooting con errores devueltos por `app.py` (memoria ausente, token mismatch, excepciones).

# DEPLOYMENT — BrakeDiscInspector

Guía de despliegue para ejecutar BrakeDiscInspector en entornos de desarrollo, laboratorio y producción. El backend es un microservicio FastAPI (PatchCore + DINOv2) y la GUI es una aplicación WPF.

---

## Índice rápido

- [Prerrequisitos](#1-prerrequisitos)
- [Despliegue local](#2-despliegue-local-desarrollo)
- [Pruebas de humo](#3-pruebas-de-humo)
- [Despliegue en laboratorio / LAN](#4-despliegue-en-laboratorio--lan-windows)
- [Producción (Linux)](#5-producción-linux)
- [Variables de entorno útiles](#6-variables-de-entorno-útiles)
- [Logging y observabilidad](#7-logging-y-observabilidad)
- [Seguridad](#8-seguridad)
- [Troubleshooting](#9-troubleshooting)
- [Checklist previo a release](#10-checklist-previo-a-release)

---

## 1) Prerrequisitos

### Backend
- Python 3.10+
- Dependencias instaladas con `pip install -r backend/requirements.txt`
- Acceso a GPU opcional (se detecta automáticamente; se puede forzar con `DEVICE=cpu`)
- Carpeta `backend/models/` persistente (local o montada)

### GUI
- Windows 10/11
- Visual Studio 2022 + .NET 8.0
- Paquetes NuGet restaurados (`OpenCvSharp4`, `CommunityToolkit.Mvvm`, etc.)
- Acceso a la carpeta `datasets/` compartida entre usuarios si se trabaja en red

---

## 2) Despliegue local (desarrollo)

### 2.1 Backend
```bash
cd backend
python -m venv .venv
source .venv/bin/activate      # PowerShell: .venv\Scripts\Activate.ps1
pip install -r requirements.txt
uvicorn backend.app:app --reload --host 127.0.0.1 --port 8000
```

### 2.2 GUI
1. Abrir `gui/BrakeDiscInspector_GUI_ROI/BrakeDiscInspector_GUI_ROI.sln` en Visual Studio.
2. Configurar `appsettings.json`:
   ```json
   {
     "Backend": {
       "BaseUrl": "http://127.0.0.1:8000",
       "DatasetRoot": "C:\\data\\brakedisc\\datasets",
       "MmPerPx": 0.20
     }
   }
   ```
3. Ejecutar el flujo dataset → `/fit_ok` → `/calibrate_ng` → `/infer` con imágenes de prueba.

---

## 3) Pruebas de humo

Con el backend en marcha:
```bash
curl http://127.0.0.1:8000/health
curl -X POST http://127.0.0.1:8000/fit_ok \
     -F role_id=Smoke \
     -F roi_id=ROI \
     -F mm_per_px=0.20 \
     -F images=@datasets/Smoke/ROI/ok/sample_ok.png
curl -X POST http://127.0.0.1:8000/infer \
     -F role_id=Smoke \
     -F roi_id=ROI \
     -F mm_per_px=0.20 \
     -F image=@datasets/Smoke/ROI/ok/sample_ok.png
```
Resultados esperados:
- `/health` devuelve `status=ok`, `device`, `model`, `version`.
- `/fit_ok` produce `n_embeddings > 0`, `coreset_size > 0`, `token_shape` consistente.
- `/infer` devuelve `score`, `heatmap_png_base64` y `regions` (puede estar vacío si no hay anomalías).【F:backend/app.py†L46-L214】

---

## 4) Despliegue en laboratorio / LAN (Windows)

### 4.1 Backend como servicio (NSSM)
1. Instalar [NSSM](https://nssm.cc/).
2. Crear servicio apuntando a `python.exe` y al comando `-m uvicorn backend.app:app --host 0.0.0.0 --port 8000`.
3. Definir `Startup directory` = ruta `backend/` y variables (`PYTHONUNBUFFERED=1`, `MODELS_DIR=D:\\brakedisc\\models`).
4. Abrir firewall para el puerto utilizado.
5. Configurar rotación de logs con `nssm set <service> AppRotateFiles 1` y `AppRotateSeconds` según política.

### 4.2 GUI
- Distribuir build MSIX o carpeta `publish` generada con `dotnet publish -c Release`.
- Configurar `Backend.BaseUrl` hacia la IP del backend (`http://192.168.1.20:8000`).
- Compartir `DatasetRoot` mediante red SMB si varios operadores contribuyen al mismo dataset.

---

## 5) Producción (Linux)

### 5.1 Backend con Gunicorn + Uvicorn worker
1. Preparar servidor (Ubuntu 22.04+ recomendado):
   ```bash
   sudo apt update && sudo apt install -y python3.10 python3.10-venv python3-pip nginx git
   ```
2. Desplegar código:
   ```bash
   sudo mkdir -p /opt/brakedisc
   sudo chown $USER:$USER /opt/brakedisc
   cd /opt/brakedisc
   git clone <repo> .
   cd backend
   python3.10 -m venv .venv
   source .venv/bin/activate
   pip install -r requirements.txt
   ```
3. Crear servicio systemd `/etc/systemd/system/brakedisc-backend.service`:
   ```ini
   [Unit]
   Description=BrakeDiscInspector Backend
   After=network.target

   [Service]
   WorkingDirectory=/opt/brakedisc/backend
   Environment="PYTHONUNBUFFERED=1" "MODELS_DIR=/var/lib/brakedisc/models" "DEVICE=auto"
   ExecStart=/opt/brakedisc/backend/.venv/bin/gunicorn \
     -k uvicorn.workers.UvicornWorker backend.app:app \
     -w 2 -b 0.0.0.0:8000
   Restart=on-failure

   [Install]
   WantedBy=multi-user.target
   ```
4. Habilitar y arrancar:
   ```bash
   sudo systemctl daemon-reload
   sudo systemctl enable brakedisc-backend
   sudo systemctl start brakedisc-backend
   ```
5. Configurar Nginx como proxy inverso (`/etc/nginx/sites-available/brakedisc`):
   ```nginx
   server {
       listen 80;
       server_name your.server.local;

       location / {
           proxy_pass http://127.0.0.1:8000;
           proxy_set_header Host $host;
           proxy_set_header X-Real-IP $remote_addr;
           proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
           proxy_set_header X-Forwarded-Proto $scheme;
       }
   }
   ```
   Activar y recargar Nginx:
   ```bash
   sudo ln -s /etc/nginx/sites-available/brakedisc /etc/nginx/sites-enabled/
   sudo nginx -t
   sudo systemctl reload nginx
   ```
6. (Opcional) Certificados TLS con `certbot --nginx`.

### 5.2 GUI en producción
- Definir `Backend.BaseUrl` con la URL HTTPS del proxy.
- Configurar `DatasetRoot` en discos locales o rutas de red según política de la planta.
- Sincronizar presets/layouts mediante control de versiones o recursos compartidos.

---

## 6) Variables de entorno útiles

| Variable | Descripción |
|----------|-------------|
| `MODELS_DIR` | Directorio donde `ModelStore` guarda `memory.npz`, `index.faiss`, `calib.json`.【F:backend/storage.py†L12-L79】 |
| `DEVICE` | Fuerza extractor a `cpu`, `cuda` o `auto`. |
| `CORESET_RATE` | Sobrescribe la tasa usada en `PatchCoreMemory.build` (0.02 por defecto). |
| `INPUT_SIZE` | Cambia el tamaño de entrada de DINOv2 (múltiplo de 14). |
| `BRAKEDISC_BACKEND_HOST`/`PORT` | Valores usados cuando se ejecuta `python backend/app.py`. |

Definirlas en `systemd`, NSSM o scripts de arranque antes de lanzar el backend.

---

## 7) Logging y observabilidad

- Backend: seguir [LOGGING.md](LOGGING.md); almacenar logs en `/var/log/brakedisc/backend.log` (Linux) o `backend\logs\backend.log` (Windows).
- GUI: habilitar logs locales (`%LOCALAPPDATA%/BrakeDiscInspector/logs/`) para correlacionar operaciones con el backend.
- Revisar Nginx (`/var/log/nginx/access.log`) o NSSM para detectar fallos de red.

---

## 8) Seguridad

- Exponer el backend tras proxy inverso y restringir accesos por firewall/VPN.
- Limitar tamaño de subida (`client_max_body_size` en Nginx, `--limit-request-field_size` si aplica).
- Mantener dependencias actualizadas (`pip install -U -r requirements.txt`).
- Usar HTTPS en entornos productivos y rotar credenciales de acceso a servidores.

---

## 9) Troubleshooting

| Problema | Diagnóstico | Solución |
|----------|-------------|----------|
| `/infer` responde `400` "Memoria no encontrada" | No se ejecutó `/fit_ok` para `(role_id, roi_id)` | Entrenar de nuevo o restaurar `memory.npz` y `index.faiss`. |
| `/infer` responde `400` "Token grid mismatch" | El ROI enviado no coincide con el `token_shape` guardado | Asegurar que la GUI exporta el mismo tamaño o reentrenar la memoria. |
| Timeouts en GUI | Subidas grandes (>100 MB) o backend en CPU | Ajustar `HttpClient.Timeout`, revisar hardware o usar GPU. |
| Nginx 502 | Servicio detenido o puerto incorrecto | `systemctl status brakedisc-backend`, revisar logs. |
| GPU no detectada | `torch.cuda.is_available()` es `False` | Instalar drivers CUDA o establecer `DEVICE=cpu`. |

---

## 10) Checklist previo a release

- [ ] Ejecutar smoke tests (`/health`, `/fit_ok`, `/infer`).
- [ ] Verificar `backend/models/<role>/<roi>/` (existencia de `memory.npz`, `calib.json`).
- [ ] Confirmar que la GUI apunta a la URL correcta (`appsettings.json` o variables de entorno).
- [ ] Revisar logs iniciales tras despliegue (backend y proxy) y capturar evidencias.
- [ ] Registrar la actualización en `docs/mcp/latest_updates.md`.

---

Para coordinación entre equipos consulta [docs/mcp/overview.md](docs/mcp/overview.md).
