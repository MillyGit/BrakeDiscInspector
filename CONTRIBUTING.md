# 📌 Actualización — 2025-10-07

**Cambios clave documentados en esta versión:**
- Se referencia el nuevo `PROJECT_OVERVIEW.txt` como material de onboarding rápido.
- Se mantiene el flujo de contribución para backend y GUI alineado con los contratos actuales.

# CONTRIBUTING — BrakeDiscInspector

Gracias por tu interés en contribuir al proyecto **BrakeDiscInspector**. Este documento describe el flujo de colaboración y las normas de estilo para el backend FastAPI y la GUI WPF. Consulta también `PROJECT_OVERVIEW.txt` para obtener una visión técnica condensada antes de iniciar cualquier tarea.

---

## Índice rápido

- [Código de conducta](#1-código-de-conducta)
- [Primeros pasos](#2-primeros-pasos)
- [Estilo de código](#3-estilo-de-código)
- [Pull Requests](#4-pull-requests)
- [Tests y validación](#5-tests-y-validación)
- [Documentación](#6-documentación)
- [Checklist antes de abrir un PR](#7-checklist-antes-de-abrir-un-pr)

---

## 1) Código de conducta

- Respeto y comunicación clara entre contribuidores.
- No incluir datos sensibles ni información propietaria.
- Issues y PRs en inglés cuando sea posible para facilitar la revisión global.

---

## 2) Primeros pasos

1. Realiza un **fork** del repositorio.
2. Clona tu fork y crea una rama descriptiva:
   ```bash
   git clone https://github.com/<tu_usuario>/BrakeDiscInspector.git
   cd BrakeDiscInspector
   git checkout -b feat/nueva-funcionalidad
   ```
3. Configura tu entorno siguiendo [DEV_GUIDE.md](DEV_GUIDE.md) y revisa `PROJECT_OVERVIEW.txt` para entender el flujo completo.

---

## 3) Estilo de código

### Backend (Python)
- PEP8 + tipado opcional (`typing` / `pydantic`).
- Imports agrupados (stdlib, third-party, local).
- Logging mediante `logging.getLogger(__name__)`.
- Evitar duplicar lógica de inferencia/persistencia ya cubierta en `infer.py`, `storage.py`.

### GUI (C# / WPF)
- Convenciones .NET (PascalCase, `_camelCase` para privados).
- `async/await` para todas las llamadas HTTP (`BackendClient`).
- Mantener adorners (`RoiAdorner`, `RoiRotateAdorner`, `ResizeAdorner`) sin cambios salvo instrucciones explícitas.
- Utilizar `ObservableCollection` para listas visibles y respetar MVVM.

### Commits
- Idioma: inglés.
- Formato recomendado (`conventional commits`):
  ```
  <type>(scope): resumen breve
  ```
  Ejemplo: `docs: update architecture overview`.

---

## 4) Pull Requests

1. Asegúrate de que tu rama esté actualizada con `main`.
2. Incluye descripción clara: propósito, cambios clave, pruebas realizadas.
3. Adjunta capturas o GIFs si hay cambios visibles en la GUI.
4. Actualiza la documentación relevante (`README.md`, `API_REFERENCE.md`, etc.).
5. Espera al menos una aprobación antes de hacer merge.

---

## 5) Tests y validación

### Backend
- Ejecuta smoke tests:
  ```bash
  curl http://127.0.0.1:8000/health
  curl -X POST http://127.0.0.1:8000/fit_ok -F role_id=Test -F roi_id=ROI -F mm_per_px=0.2 -F images=@sample_ok.png
  curl -X POST http://127.0.0.1:8000/infer -F role_id=Test -F roi_id=ROI -F mm_per_px=0.2 -F image=@sample_ok.png
  ```
- Si añades lógica nueva, crea tests en `backend/tests/` (PyTest) y documenta cómo ejecutarlos.

### GUI
- Compila solución (`Build > Build Solution`).
- Verifica flujo dataset → `/fit_ok` → `/calibrate_ng` → `/infer` con muestras de prueba.
- Comprueba que los heatmaps se muestran alineados y que la app maneja errores HTTP.

---

## 6) Documentación

- Mantén el README, `PROJECT_OVERVIEW.txt` y las guías actualizadas con cualquier cambio relevante.
- Añade diagramas o ejemplos cuando simplifiquen la comprensión.
- Registra eventos importantes en `docs/mcp/latest_updates.md` cuando afecten contratos o despliegues.

---

## 7) Checklist antes de abrir un PR

- [ ] Código formateado y sin warnings críticos.
- [ ] Tests/manual smoke realizados y descritos en el PR.
- [ ] Documentación actualizada (si aplica).
- [ ] Sin cambios accidentales en archivos generados (`*.png`, `*.log`, etc.).
- [ ] Referencias a issues vinculadas (`Fixes #123`).

---

¡Gracias por contribuir a BrakeDiscInspector! 🚀
