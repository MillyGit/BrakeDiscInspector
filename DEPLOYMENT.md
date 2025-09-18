
# DEPLOYMENT — BrakeDiscInspector

Guía de despliegue para entornos **local**, **LAN/laboratorio** y **producción** (on‑prem o VM). Incluye pruebas de humo, logging y resolución de problemas.

---

## 1) Pre‑requisitos

- **Backend**
  - Python 3.10+
  - `pip install -r backend/requirements.txt`
  - Modelo TensorFlow en `backend/model/current_model.h5`
  - Umbral en `backend/model/threshold.txt` (ej. `0.57`)

- **GUI**
  - Windows 10/11
  - .NET SDK 8.0
  - Visual Studio 2022
  - NuGet: OpenCvSharp4, runtime.win, Extensions

---

## 2) Despliegue local (desarrollo)

### 2.1 Backend
```bash
cd backend
python -m venv .venv
.venv\Scripts\activate          # PowerShell en Windows
pip install -r requirements.txt
python app.py                     # inicia en http://127.0.0.1:5000
```

### 2.2 GUI
- Abre `gui/BrakeDiscInspector_GUI_ROI.sln` y compila.
- Verifica `gui/BrakeDiscInspector_GUI_ROI/appsettings.json`:
  ```json
  { "Backend": { "BaseUrl": "http://127.0.0.1:5000" } }
  ```

---

## 3) Pruebas de humo (smoke tests)

Con el backend corriendo:
```bash
# Estado
curl http://127.0.0.1:5000/train_status

# Análisis simple
curl -X POST http://127.0.0.1:5000/analyze -F "file=@samples/crop.png"
```

Resultados esperados:
- `/train_status` devuelve JSON con `status`/`threshold`.
- `/analyze` devuelve JSON con `label/score/threshold/heatmap_png_b64`.

---

## 4) Despliegue LAN/Lab (Windows)

### 4.1 Backend como servicio (NSSM)
1. Instala [NSSM](https://nssm.cc/).
2. Crear servicio:
   ```powershell
   nssm install BrakeDiscInspectorBackend
   # Path: C:\Python\python.exe
   # Arguments: app.py
   # Startup directory: C:\ruta\a\backend
   ```
3. Iniciar servicio:
   ```powershell
   nssm start BrakeDiscInspectorBackend
   ```
4. Abrir firewall para el puerto (p.ej. 5000).

### 4.2 GUI
- Distribuye el ejecutable WPF o ejecuta desde Visual Studio.
- Ajusta `BaseUrl` al host del backend dentro de la LAN, por ejemplo:
  ```json
  { "Backend": { "BaseUrl": "http://192.168.1.20:5000" } }
  ```

---

## 5) Producción (Linux/VM)

### 5.1 Backend con Gunicorn + Reverse Proxy (Nginx)

**Gunicorn** (como WSGI runner) y **Nginx** como proxy/estático/SSL.

1) Instalar dependencias del sistema (ejemplo Ubuntu):
```bash
sudo apt update && sudo apt install -y python3-pip python3-venv nginx
```

2) Entorno virtual + deps:
```bash
cd /opt/brakedisc/backend
python3 -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt
```

3) Gunicorn unit (systemd): `/etc/systemd/system/brakedisc.service`
```
[Unit]
Description=BrakeDiscInspector Backend (Gunicorn)
After=network.target

[Service]
User=www-data
Group=www-data
WorkingDirectory=/opt/brakedisc/backend
Environment="PYTHONUNBUFFERED=1"
ExecStart=/opt/brakedisc/backend/.venv/bin/gunicorn -w 2 -b 0.0.0.0:5000 app:app
Restart=always

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable brakedisc
sudo systemctl start brakedisc
```

4) Nginx host `/etc/nginx/sites-available/brakedisc`:
```
server {
    listen 80;
    server_name your.server.local;

    location / {
        proxy_pass http://127.0.0.1:5000;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```
```bash
sudo ln -s /etc/nginx/sites-available/brakedisc /etc/nginx/sites-enabled/brakedisc
sudo nginx -t
sudo systemctl reload nginx
```

5) (Opcional) HTTPS con Certbot:
```bash
sudo apt install -y certbot python3-certbot-nginx
sudo certbot --nginx -d your.server.local
```

### 5.2 GUI apuntando a producción
Actualizar `appsettings.json` en cada estación:
```json
{ "Backend": { "BaseUrl": "https://your.server.local" } }
```

---

## 6) Variables de entorno (opcional)

- `INPUT_SIZE` (si se parametriza en `app.py`).
- `MODEL_PATH` para ubicar modelo alternativo.
- `FLASK_ENV=production` (si se arranca con Flask directamente).

---

## 7) Logging y observabilidad

- **Backend**: `backend/logs/backend.log` (configurado por `logging.basicConfig`).
- **Nginx/Gunicorn**: `/var/log/nginx/*`, journal `systemd`.
- Considerar rotación de logs (`logrotate`) y niveles por entorno.

---

## 8) Seguridad

- Restringir el acceso del backend a red interna (Nginx delante).
- HTTPS en proxy inverso.
- Firewall de host y/o grupo de seguridad (cloud).
- Aislamiento de usuario (`www-data`) en systemd unit.
- Validación de tamaño de archivos subidos (Flask `MAX_CONTENT_LENGTH`).

---

## 9) Solución de problemas

- **404/502 en Nginx**: comprobar servicio `brakedisc` (`systemctl status brakedisc`) y `proxy_pass`.
- **OOM/alto consumo**: reducir workers (`-w 2`), activar swap moderado, revisar tamaño del modelo.
- **Permisos de modelo**: usuario del servicio debe poder leer `/opt/brakedisc/backend/model/*`.
- **CORS**: habilitado en Flask con `flask-cors`; revisar cabeceras si hay bloqueo en navegadores.

---

## 10) Checklist de despliegue

- [ ] Backend instalado y servicio activo (`curl /train_status` OK).
- [ ] Nginx proxy inverso (o firewall abierto si exposición directa).
- [ ] HTTPS configurado (si aplica).
- [ ] Rutas de logs verificadas.
- [ ] GUI configurada con `BaseUrl` correcto y prueba de `Analyze` con imagen real.
