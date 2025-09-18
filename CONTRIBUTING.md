
# CONTRIBUTING — BrakeDiscInspector

Gracias por tu interés en contribuir al proyecto **BrakeDiscInspector**.  
Este documento define las reglas y el flujo de trabajo para colaborar en el repositorio.

---

## 1) Código de conducta

- Respeto mutuo entre contribuidores.
- Feedback técnico y constructivo, no personal.
- No incluir datos sensibles (clientes, hardware propietario, etc.).
- Issues y PRs en inglés preferiblemente para mayor alcance.

---

## 2) Cómo empezar

1. **Fork** del repositorio en GitHub.  
2. **Clonar** tu fork:
   ```bash
   git clone https://github.com/<tu_usuario>/BrakeDiscInspector.git
   cd BrakeDiscInspector
   ```
3. Crear rama de feature/fix:
   ```bash
   git checkout -b feature/mi-mejora
   ```

---

## 3) Estilo de código

### 3.1 Backend (Python)
- Seguir [PEP8](https://peps.python.org/pep-0008/).
- Imports ordenados (`stdlib`, `third-party`, `local`).
- Nombrado:
  - Funciones: `snake_case`
  - Clases: `PascalCase`
- Documentar funciones públicas con docstrings.

### 3.2 GUI (C#)
- Convenciones .NET:
  - Clases y métodos: `PascalCase`
  - Campos privados: `_camelCase`
  - Propiedades públicas: `PascalCase`
- Usar `var` para inferencia local donde sea claro.
- Mantener separación clara entre lógica GUI y llamadas al backend.

### 3.3 Commits
- Idioma: inglés
- Formato recomendado:
  ```
  <type>(scope): breve descripción

  [opcional cuerpo explicativo]
  ```
- Tipos comunes:
  - `feat`: nueva funcionalidad
  - `fix`: corrección de bug
  - `docs`: documentación
  - `refactor`: cambios internos sin modificar funcionalidad
  - `test`: tests añadidos o modificados
  - `chore`: mantenimiento/infraestructura

Ejemplo:
```
feat(gui): add rotate handle to ROI adorner
```

---

## 4) Flujo de trabajo de Pull Request (PR)

1. Crear rama desde `main`.
2. Implementar cambios y asegurarse de que compila y pasa smoke tests.
3. Actualizar documentación asociada (ej. `API_REFERENCE.md`, `ROI_AND_MATCHING_SPEC.md`).
4. Hacer commit y push a tu fork.
5. Abrir un PR contra `main` del repositorio principal.
6. Describir claramente:
   - Propósito del cambio
   - Archivos modificados
   - Screenshots (si aplica)
   - Estado de pruebas locales

### 4.1 Revisiones
- Al menos 1 revisor debe aprobar antes de merge.
- Resolver todos los comentarios de revisión.

### 4.2 CI/CD (futuro)
- Los PRs deben pasar linters y pruebas automáticas (cuando se configuren workflows).

---

## 5) Issues

- Usa [GitHub Issues](https://github.com/<org>/BrakeDiscInspector/issues).
- Plantilla recomendada:
  - **Descripción clara**
  - **Pasos para reproducir**
  - **Comportamiento esperado**
  - **Logs o capturas relevantes**

Labels sugeridos:
- `bug`
- `enhancement`
- `documentation`
- `question`

---

## 6) Tests y validación

### 6.1 Backend
- Ejecutar manualmente endpoints con `curl`:
  ```bash
  curl http://127.0.0.1:5000/train_status
  curl -X POST http://127.0.0.1:5000/analyze -F "file=@samples/crop.png"
  ```

### 6.2 GUI
- Compilar solución en Visual Studio.
- Probar flujo: cargar imagen, dibujar ROI, rotar, analizar.

---

## 7) Documentación

- Documentación en Markdown (`docs/*.md`).
- Lenguaje simple y preciso, con ejemplos prácticos.
- Mantener README.md como entrypoint actualizado.

---

## 8) Checklist para contribuir

- [ ] Código sigue estilos definidos.
- [ ] Commits limpios y descriptivos.
- [ ] PR incluye documentación actualizada si aplica.
- [ ] Cambios probados localmente (backend/GUI).
- [ ] Issues relacionados referenciados en PR.

---

## 9) Reconocimientos

Todas las contribuciones se reconocen en el historial de commits y en los PRs.  
Gracias por ayudar a mejorar **BrakeDiscInspector** 🚀
