# CI/CD

La integración continua se gestiona mediante GitHub Actions (`.github/workflows/`). El objetivo es validar el backend sin requerir GPU y asegurar que las rutas críticas permanecen estables.

## Estrategia general
- Ejecutar el workflow solo cuando cambien archivos relevantes (`backend/**`, `docs/**`, `.github/workflows/**`).
- Preparar un entorno Python controlado (3.11) con dependencias fijadas.
- Ejecutar pruebas unitarias (`pytest`) y validaciones estáticas ligeras (opcional `flake8`, `mypy`).

## Pasos recomendados en el workflow
```yaml
jobs:
  backend-tests:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-python@v5
        with:
          python-version: '3.11'
      - name: Instalar dependencias mínimas
        run: |
          python -m venv .venv
          source .venv/bin/activate
          pip install --upgrade pip
          pip install --index-url https://download.pytorch.org/whl/cpu \
            torch==2.5.1+cpu torchvision==0.20.1+cpu
          pip install -r backend/requirements.txt
      - name: Limpiar TensorFlow
        run: |
          source .venv/bin/activate
          pip uninstall -y tensorflow tensorflow-cpu tensorflow-intel || true
      - name: Ejecutar tests backend
        env:
          PYTHONPATH: ${{ github.workspace }}/backend
          BDI_LIGHT_IMPORT: '1'
        run: |
          source .venv/bin/activate
          python -m pytest backend/tests
```

## Notas importantes
- **Torch CPU**: usar ruedas CPU evita dependencias CUDA en runners sin GPU.
- **Desinstalar TensorFlow**: previene conflictos (`tensorflow.__spec__ is None`) con `torchvision` al importar.
- **BDI_LIGHT_IMPORT**: desactiva imports pesados y hace que los tests se ejecuten en segundos.
- **PYTHONPATH**: apuntar a `backend/` simplifica los imports relativos en pruebas.

## Extensiones opcionales
- Agregar un job de `lint` (`flake8`, `black --check`) para mantener estilo consistente.
- Publicar artefactos (logs, reportes pytest-html) en caso de fallo para facilitar debugging.
- Integrar escaneos de seguridad (`pip-audit`) en ejecuciones semanales.

## CD / Despliegue
Aunque no hay pipeline automatizado de despliegue, se recomienda:
- Construir imágenes Docker del backend con las dependencias fijadas.
- Publicar la imagen en un registro interno y desplegar en estaciones GPU.
- Versionar la GUI y backend en paralelo para garantizar compatibilidad (usar tags `gui-vX.Y`, `backend-vX.Y`).
