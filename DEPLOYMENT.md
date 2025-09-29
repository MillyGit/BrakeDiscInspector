# DEPLOYMENT — BrakeDiscInspector

Guía de despliegue para ejecutar BrakeDiscInspector en entornos de desarrollo, laboratorio y producción. El backend es un microservicio FastAPI (PatchCore + DINOv2) y la GUI es una aplicación WPF.

---

## 1) Prerrequisitos

### Backend
- Python 3.10+
- Dependencias instaladas con `pip install -r backend/requirements.txt`
- Acceso a GPU opcional (funciona en CPU)
- Directorio `backend/models/` persistente para artefactos

### GUI
- Windows 10/11
- .NET 8.0 + Visual Studio 2022
- Paquetes NuGet restaurados (`OpenCvSharp4`, `CommunityToolkit.Mvvm`, etc.)

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
1. Abrir `gui/BrakeDiscInspector_GUI_ROI.sln` en Visual Studio.
2. Configurar `appsettings.json`:
   ```json
   {
     "Backend": {
       "BaseUrl": "http://127.0.0.1:8000",
       "DatasetRoot": "C:\\data\\brakedisc\\datasets"
     }
   }
   ```
3. Ejecutar la app, crear dataset y probar el flujo dataset → fit → calibrate → infer.

---

## 3) Pruebas de humo

Con el backend en marcha:
```bash
curl http://127.0.0.1:8000/health
curl -X POST http://127.0.0.1:8000/fit_ok -F role_id=Smoke -F roi_id=ROI -F mm_per_px=0.2 -F images=@sample_ok.png
curl -X POST http://127.0.0.1:8000/infer -F role_id=Smoke -F roi_id=ROI -F mm_per_px=0.2 -F image=@sample_ok.png
```

Resultados esperados:
- `/health` responde `status=ok` y dispositivo (`cpu`/`cuda`).
- `/fit_ok` devuelve `n_embeddings > 0` y `coreset_size > 0`.
- `/infer` produce `score`, `heatmap_png_base64` y `regions` (aunque esté vacío si no hay anomalías).

---

## 4) Despliegue en laboratorio / LAN (Windows)

### 4.1 Backend como servicio (NSSM)
1. Instalar [NSSM](https://nssm.cc/).
2. Crear servicio apuntando a `python.exe` y al script `-m uvicorn backend.app:app --host 0.0.0.0 --port 8000`.
3. Configurar `Startup directory` al path de `backend/` y variables de entorno necesarias (`PYTHONUNBUFFERED=1`).
4. Abrir firewall para el puerto 8000 (o el elegido).

### 4.2 GUI
- Distribuir build de WPF o ejecutar desde Visual Studio.
- Actualizar `BaseUrl` con la IP del backend en la LAN (ej. `http://192.168.1.20:8000`).

---

## 5) Producción (Linux)

### 5.1 Backend con Gunicorn + Uvicorn Worker

1. Instalar dependencias del sistema:
   ```bash
   sudo apt update && sudo apt install -y python3.10 python3.10-venv python3-pip nginx
   ```
2. Configurar entorno:
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
3. Crear servicio systemd `/etc/systemd/system/brakedisc.service`:
   ```ini
   [Unit]
   Description=BrakeDiscInspector Backend
   After=network.target

   [Service]
   User=brakedisc
   Group=brakedisc
   WorkingDirectory=/opt/brakedisc/backend
   Environment="PYTHONUNBUFFERED=1"
   ExecStart=/opt/brakedisc/backend/.venv/bin/gunicorn \
     -k uvicorn.workers.UvicornWorker backend.app:app \
     -w 2 -b 0.0.0.0:8000
   Restart=on-failure

   [Install]
   WantedBy=multi-user.target
   ```
4. Habilitar y arrancar el servicio:
   ```bash
   sudo systemctl daemon-reload
   sudo systemctl enable brakedisc
   sudo systemctl start brakedisc
   ```
5. Configurar Nginx `/etc/nginx/sites-available/brakedisc`:
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
   ```bash
   sudo ln -s /etc/nginx/sites-available/brakedisc /etc/nginx/sites-enabled/
   sudo nginx -t
   sudo systemctl reload nginx
   ```
6. (Opcional) Configurar HTTPS con Certbot:
   ```bash
   sudo apt install -y certbot python3-certbot-nginx
   sudo certbot --nginx -d your.server.local
   ```

### 5.2 GUI en producción
- Actualizar `appsettings.json` con la URL HTTPS del backend.
- Configurar rutas de dataset locales o de red según política de planta.

---

## 6) Variables de entorno útiles

| Variable | Descripción |
|----------|-------------|
| `INPUT_SIZE` | Sobrescribe el tamaño de entrada usado por DINOv2 (por defecto 448). |
| `CORESET_RATE` | Ajusta el porcentaje de coreset (0.02 por defecto). |
| `MODELS_DIR` | Directorio donde guardar artefactos (`models/`). |

Se pueden definir en el entorno del servicio (`systemd`, NSSM) antes de lanzar el backend.

---

## 7) Logging y observabilidad

- Backend: revisar `LOGGING.md` para eventos mínimos y uso de `X-Correlation-Id`.
- GUI: habilitar logs locales (`gui/logs/gui.log`) para correlacionar con el backend.
- Nginx/Gunicorn: monitorear `journalctl -u brakedisc` y `/var/log/nginx/access.log`.

---

## 8) Seguridad

- Exponer el backend únicamente tras un proxy inverso (Nginx) con HTTPS.
- Limitar acceso por firewall / grupos de seguridad.
- Validar tamaños máximos de subida (FastAPI `UploadFile` + reverse proxy `client_max_body_size`).
- Mantener dependencias actualizadas (`pip install -U -r requirements.txt`).

---

## 9) Troubleshooting

| Problema | Diagnóstico | Solución |
|----------|-------------|----------|
| `/infer` devuelve 400 “Memoria no encontrada” | No se ha ejecutado `/fit_ok` para ese rol/ROI | Entrenar nuevamente o copiar artefactos previos a `models/<role>/<roi>/`. |
| GPU no detectada | `torch.cuda.is_available()` retorna `False` | Instalar versión CUDA de PyTorch o forzar `DEVICE=cpu`. |
| Timeouts en GUI | Requests largos (datasets grandes) | Aumentar timeout en `HttpClient` y monitorear ancho de banda. |
| Nginx 502 | Backend caído o puerto incorrecto | Revisar `systemctl status brakedisc` y logs. |

---

## 10) Checklist previo a release

- [ ] Ejecutar smoke tests (`/health`, `/fit_ok`, `/infer`).
- [ ] Verificar que `models/<role>/<roi>/` contiene `memory.npz` y, si aplica, `calib.json`.
- [ ] Confirmar que la GUI apunta al backend correcto (`appsettings.json`).
- [ ] Registrar evidencias en `docs/mcp/latest_updates.md` según el MCP.
- [ ] Revisar logs post-despliegue (backend y proxy) durante la primera hora.

---

Para coordinación entre equipos (dataset, backend, GUI) consulta [docs/mcp/overview.md](docs/mcp/overview.md).
