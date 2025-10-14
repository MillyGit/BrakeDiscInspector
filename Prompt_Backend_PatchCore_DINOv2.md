# 📌 Actualización — 2025-10-07

**Cambios clave documentados en esta versión:**
- Prompt alineado con el backend actual basado en FastAPI (`app.py`) y módulos (`features.py`, `patchcore.py`, `infer.py`, `calib.py`, `storage.py`).
- Se refuerza el uso obligatorio de DINOv2 ViT-S/14, PatchCore y persistencia por `(role_id, roi_id)` (`backend/models/<role>/<roi>/`).
- Se detallan los contratos de los endpoints `/health`, `/fit_ok`, `/calibrate_ng`, `/infer` y las expectativas de logging/observabilidad.

# Prompt para agentes — Backend PatchCore + DINOv2 (sin tocar la GUI)

Este prompt guía a cualquier agente/desarrollador que deba mantener o extender el backend de BrakeDiscInspector. La GUI WPF ya provee ROIs canónicos; el backend sólo debe procesarlos con PatchCore + DINOv2 respetando los contratos establecidos.

---

## 1. Objetivo técnico

Implementar y mantener un microservicio FastAPI capaz de:
1. Recibir ROIs canónicos (PNG/JPG) junto con `role_id`, `roi_id`, `mm_per_px` y máscaras opcionales (`shape`).
2. Extraer embeddings con **DINOv2 ViT-S/14 congelado** (`timm`, modelo `vit_small_patch14_dinov2.lvd142m`).
3. Construir memoria **PatchCore** (coreset k-center greedy + kNN FAISS opcional) a partir de muestras OK (`/fit_ok`).
4. Calibrar thresholds con scores OK/NG (`/calibrate_ng`).
5. Inferir anomalías (`/infer`) devolviendo `score`, `threshold`, `heatmap_png_base64`, `regions`, `token_shape`.
6. Persistir artefactos por `(role_id, roi_id)` en `backend/models/<role>/<roi>/` (`memory.npz`, `index.faiss`, `calib.json`).

**Restricciones**: no modificar la GUI, no introducir nuevos endpoints ni alterar la firma de los existentes. Mantener compatibilidad con Python 3.10+.

---

## 2. Arquitectura del backend

```
backend/
  app.py          # FastAPI (endpoints, validaciones, heatmap PNG)
  features.py     # DinoV2Features (timm) - input_size múltiplo de 14
  patchcore.py    # PatchCoreMemory (coreset + kNN)
  infer.py        # InferenceEngine (heatmap, percentiles, contornos)
  calib.py        # choose_threshold (percentiles OK/NG)
  roi_mask.py     # build_mask rect/circle/annulus
  storage.py      # ModelStore (memory.npz, index.faiss, calib.json)
  requirements.txt
  models/<role>/<roi>/
     memory.npz
     index.faiss (opcional)
     calib.json
```

### 2.1 `app.py`
- Define `/health`, `/fit_ok`, `/calibrate_ng`, `/infer`.
- Decodifica imágenes (`cv2.imdecode`), valida `token_shape`, reconstruye memoria desde disco y normaliza respuestas.【F:backend/app.py†L46-L214】

### 2.2 `features.py`
- `DinoV2Features` carga `vit_small_patch14_dinov2.lvd142m` desde `timm`, rescalea a `input_size` (448 por defecto) y devuelve embeddings + `token_shape` (`Ht`, `Wt`).【F:backend/features.py†L1-L200】

### 2.3 `patchcore.py`
- `PatchCoreMemory.build(E, coreset_rate)` selecciona un coreset con k-center greedy y construye kNN (FAISS si disponible).【F:backend/patchcore.py†L1-L200】

### 2.4 `infer.py`
- `InferenceEngine.run` aplica kNN, reescala heatmap, enmascara (`roi_mask`), calcula percentil (`score`) y filtra contornos por `area_mm2_thr` usando `mm_per_px`. Devuelve `heatmap_u8`, `regions`, `token_shape`.【F:backend/infer.py†L17-L181】

### 2.5 `calib.py`
- `choose_threshold(ok_scores, ng_scores, percentile=99)` calcula thresholds entre `p99_ok` y `p5_ng` cuando hay NG disponibles.【F:backend/calib.py†L1-L120】

### 2.6 `storage.py`
- `ModelStore` guarda/carga `memory.npz` (embeddings, token shape, metadata), `index.faiss` y `calib.json`. Rutas: `models/<role>/<roi>/`.【F:backend/storage.py†L12-L79】

---

## 3. Contrato HTTP (FastAPI)

| Endpoint | Método | Descripción |
|----------|--------|-------------|
| `/health` | GET | Estado del servicio, dispositivo, modelo, versión. |
| `/fit_ok` | POST (multipart) | Entrena memoria PatchCore a partir de imágenes OK. Devuelve `n_embeddings`, `coreset_size`, `token_shape`, ratios. |
| `/calibrate_ng` | POST (JSON) | Calcula y guarda `threshold`, percentiles, `mm_per_px`, `area_mm2_thr`. |
| `/infer` | POST (multipart) | Ejecuta inferencia: devuelve `score`, `threshold` (nullable), `heatmap_png_base64`, `regions`, `token_shape`. |

Errores se devuelven como `{ "error": "mensaje", "trace": "stacktrace" }` con códigos 4xx/5xx.【F:backend/app.py†L108-L214】

---

## 4. Persistencia y datasets

- **Backend**: `models/<role>/<roi>/memory.npz`, `index.faiss`, `calib.json`. Cada `memory.npz` incluye `emb` (float32) y `token_h/w`.
- **GUI**: exporta `datasets/<role>/<roi>/<ok|ng>/SAMPLE_*.png` + `.json` con `role_id`, `roi_id`, `mm_per_px`, `shape_json`, `angle`, `timestamp`. No tocar esta estructura; el backend se apoya en ella para entrenar.

---

## 5. Buenas prácticas

1. **GPU/CPU**: respetar `DEVICE` (`auto` por defecto). El código debe funcionar sin GPU; usar `torch.cuda.is_available()` solo dentro de `features.py`.
2. **Coreset**: exponer `CORESET_RATE` configurable (0.02 por defecto). Validar que `token_shape` coincide entre entrenamiento/inferencia.
3. **Logging**: usar `logging.getLogger(__name__)`, registrar inicio, `/fit_ok`, `/calibrate_ng`, `/infer`, y propagar `X-Correlation-Id` si se recibe. Ver [LOGGING.md](LOGGING.md).
4. **Errores claros**: devolver `400` en lugar de `500` para condiciones controladas (`Token grid mismatch`, memoria ausente).
5. **Tests manuales**: siempre ejecutar smoke tests (`/health`, `/fit_ok`, `/infer`) tras cambios.

---

## 6. Checklist para modificaciones

- [ ] Mantener estables rutas y nombres de campos (`role_id`, `roi_id`, `mm_per_px`, `shape`).
- [ ] Actualizar documentación relevante (`API_REFERENCE.md`, `DATA_FORMATS.md`) si se añade funcionalidad.
- [ ] Registrar nuevos parámetros en `DEV_GUIDE.md` / `DEPLOYMENT.md` si afectan despliegues.
- [ ] Garantizar compatibilidad con modelos guardados (`memory.npz` existentes) o documentar migraciones.
- [ ] Añadir logs y manejar excepciones con mensajes accionables.

---

## 7. Fuera de alcance

- No cambiar la GUI, adorners o pipeline de canonicalización.
- No introducir nuevos frameworks de deep learning (seguir con PyTorch + timm + PatchCore).
- No eliminar FAISS opcional; mantener fallback a `sklearn` si FAISS no está presente.

---

Siguiendo estas directrices, el backend seguirá alineado con la GUI y con los contratos documentados a octubre de 2025.
