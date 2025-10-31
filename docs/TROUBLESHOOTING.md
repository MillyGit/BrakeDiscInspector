# Troubleshooting

## Problemas comunes y soluciones

### Pylance “Import no resuelto”
- Selecciona el intérprete correcto en VS Code (`Ctrl+Shift+P → Python: Select Interpreter`).
- Activa el entorno virtual antes de abrir el editor.
- Ejecuta `python -c "import cv2, fastapi, torch"` para verificar que las dependencias están instaladas.

### OpenCV `libGL` faltante (Linux/WSL)
- Instala las bibliotecas gráficas mínimas:
  ```bash
  sudo apt-get install -y libgl1
  ```
- Reinicia la sesión para asegurarte de que el loader encuentre la librería.

### Conflictos con instalaciones previas de TensorFlow
- Si compartes entorno con otros proyectos que aún usan TensorFlow, desinstálalo antes de instalar Torch CPU:
  ```bash
  pip uninstall -y tensorflow tensorflow-cpu tensorflow-intel || true
  ```
- El backend actual no requiere TensorFlow, pero su presencia puede interferir con `torchvision`.

### Botones de la GUI no visibles / toolbars superpuestas
- Verifica que el `ToolBarTray` tenga `Band` distintos para cada toolbar.
- Ajusta `Panel.ZIndex` de las toolbars a un valor alto (`>=1000`).
- Evita aplicar `Foreground` a contenedores como `StackPanel`; establece estilos dentro de cada control.

### Backend tarda en responder
- Confirma que `torch.cuda.is_available()` devuelve `True` si esperas ejecución en GPU.
- Activa `memory_fit=true` únicamente si hay suficiente RAM; de lo contrario la inicialización puede demorar.
- Revisa los logs del backend (nivel INFO) para detectar cuellos de botella.

### `pytest` no encuentra módulos del backend
- Ejecuta las pruebas desde la raíz o exporta `PYTHONPATH=backend`.
- Usa `python -m pytest backend/tests -k app_fastapi` para aislar casos de los endpoints actuales.

### Los archivos guardados no aparecen en el explorador
- Usa el botón **Open Folder** en la GUI para abrir el directorio actual.
- Verifica permisos de escritura en la carpeta `data/`.
- Si trabajas en WSL, confirma la ruta compartida entre Windows y Linux.

## Pasos de diagnóstico rápido
1. `curl http://localhost:8000/health` para asegurar que el backend está activo.
2. Ejecutar `python -m pytest backend/tests` para validar la capa FastAPI sin cargar modelos pesados.
3. Revisar el panel de logs de la GUI para mensajes recientes.

## Recursos adicionales
- [docs/SETUP.md](SETUP.md) para instalación detallada.
- [docs/CI_CD.md](CI_CD.md) para configuración de workflows.
- Issues del repositorio (`github.com/MillyGit/BrakeDiscInspector/issues`) para reportar fallos no cubiertos.
