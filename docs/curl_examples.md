# Ejemplos `curl`

## Health
```bash
curl http://127.0.0.1:8000/health
```

## fit_ok (multipart, varias imágenes OK)
```bash
curl -X POST http://127.0.0.1:8000/fit_ok \
  -F role_id=R1 \
  -F roi_id=ROI_A \
  -F mm_per_px=0.20 \
  -F images=@ok1.jpg \
  -F images=@ok2.jpg \
  -F memory_fit=false
```

## calibrate_ng (JSON)
```bash
curl -X POST http://127.0.0.1:8000/calibrate_ng \
  -H "Content-Type: application/json" \
  -d '{
        "role_id": "R1",
        "roi_id": "ROI_A",
        "mm_per_px": 0.20,
        "ok_scores": [0.01, 0.02, 0.015],
        "ng_scores": [0.35, 0.40],
        "area_mm2_thr": 1.0,
        "score_percentile": 99
      }'
```

## infer (multipart, `shape` opcional)
```bash
curl -X POST http://127.0.0.1:8000/infer \
  -F role_id=R1 -F roi_id=ROI_A -F mm_per_px=0.20 \
  -F image=@test.jpg

# Con shape circular:
curl -X POST http://127.0.0.1:8000/infer \
  -F role_id=R1 -F roi_id=ROI_A -F mm_per_px=0.20 \
  -F image=@test.jpg \
  -F 'shape={"kind":"circle","cx":512,"cy":512,"r":480}'

# Con annulus:
curl -X POST http://127.0.0.1:8000/infer \
  -F role_id=R1 -F roi_id=ROI_A -F mm_per_px=0.20 \
  -F image=@test.jpg \
  -F 'shape={"kind":"annulus","cx":512,"cy":512,"r":480,"r_inner":440}'
```
