# Guía de Setup

Esta guía cubre la instalación del backend y la preparación de la GUI en entornos Windows y WSL/Ubuntu, con foco en ejecutar el backend con soporte GPU cuando sea posible.

## Prerrequisitos comunes
- Git para clonar el repositorio.
- Python 3.11 o 3.12 (recomendado crear un entorno virtual).
- .NET 6+ SDK para compilar/ejecutar la GUI WPF.
- Drivers NVIDIA actualizados si se utilizará GPU.

## Instalación en Windows (con GPU opcional)
1. **Clonar repositorio**
   ```bash
   git clone https://github.com/MillyGit/BrakeDiscInspector.git
   cd BrakeDiscInspector
   ```
2. **Crear entorno virtual**
   ```bash
   cd backend
   python -m venv .venv
   .venv\Scripts\activate
   ```
3. **Instalar PyTorch**
   - **CPU**
     ```bash
     pip install --index-url https://download.pytorch.org/whl/cpu \
       torch==2.5.1+cpu torchvision==0.20.1+cpu
     ```
   - **CUDA 12.1**
     ```bash
     pip install --index-url https://download.pytorch.org/whl/cu121 \
       torch==2.5.1+cu121 torchvision==0.20.1+cu121
     ```
4. **Instalar dependencias restantes**
   ```bash
   pip install -r requirements.txt
   ```
5. **Lanzar backend**
   ```bash
   uvicorn app:app --host 0.0.0.0 --port 8000 --reload
   ```
6. **Configurar GUI**
   - Abrir la solución WPF (`gui/BrakeDiscInspector_GUI_ROI`) en Visual Studio o VS Code.
   - Asegurar que el proyecto apunte al backend (`http://localhost:8000` por defecto).

## Instalación en WSL/Ubuntu
1. **Dependencias del sistema**
   ```bash
   sudo apt-get update && sudo apt-get install -y python3-venv libgl1
   ```
   `libgl1` es necesario para que OpenCV renderice.
2. **Entorno virtual e instalación**
   ```bash
   cd BrakeDiscInspector/backend
   python3 -m venv .venv
   source .venv/bin/activate
   pip install --upgrade pip
   pip install --index-url https://download.pytorch.org/whl/cpu \
     torch==2.5.1+cpu torchvision==0.20.1+cpu
   pip install -r requirements.txt
   ```
3. **Ejecutar backend**
   ```bash
   uvicorn app:app --host 0.0.0.0 --port 8000 --reload
   ```
4. **Acceso desde Windows**
   - Configurar la GUI para apuntar a `http://localhost:8000` si se usa WSLg o al puerto publicado (`http://<ip_wsl>:8000`).

## Variables de entorno útiles
- `BDI_LIGHT_IMPORT=1`: evita cargar `timm/torchvision` en CI o máquinas sin GPU (lazy import).
- `BDI_DATA_ROOT=/ruta/custom`: cambia la carpeta base donde se guardan datasets.
- `UVICORN_RELOAD_DIRS=backend`: restringe los watchers de recarga.

## Verificación rápida
```bash
# Desde backend/
python -m pytest tests/test_app_train_status.py
curl http://localhost:8000/
curl http://localhost:8000/train/status
```

## Solución de problemas
- Si `torchvision` falla por dependencias, reinstalar con la misma index URL que `torch`.
- Si OpenCV lanza error `libGL`, instalar `libgl1` (WSL/Linux).
- Pylance “Import no resuelto”: seleccionar el intérprete correcto en VS Code (`Ctrl+Shift+P > Python: Select Interpreter`).

## Recomendaciones adicionales
- Mantener el repositorio sincronizado (`git pull`) antes de entrenamientos para recoger ajustes de pipeline.
- Realizar backups periódicos de `data/` y versionar configuraciones críticas.
- Utilizar `requirements-lock.txt` (si existe) para replicar entornos productivos.
