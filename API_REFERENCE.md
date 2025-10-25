API de Backend (FastAPI)

Todas las rutas devuelven JSON. Usar Content-Type: application/json. Habilitar CORS para POST en /fit_ok y /infer.

GET /health

200 OK si el servidor está listo.

POST /infer

Body:

{
  "image_path": "C:/data/images/frame_0001.png",
  "roi": {
    "shape": "square|circle|annulus",
    "center": [x, y],
    "size": [w, h],
    "radius": r,
    "inner_radius": ir,
    "rotation_deg": a
  },
  "model_key": "inspection-1",
  "threshold": 0.5
}


Resp:

{
  "score": 0.13,
  "ok": true,
  "threshold": 0.51,
  "heatmap_path": "C:/.../hm/inspection-1_0001.png",
  "regions": [ { "x": 10, "y": 12, "w": 15, "h": 10, "score": 0.8 } ]
}

POST /fit_ok

Entrena memoria OK del modelo model_key desde carpetas OK.
Body:

{
  "model_key": "inspection-1",
  "dataset_ok_dir": "C:/datasets/inspection-1/ok"
}


Resp: {"status":"ok","fitted":N}

CORS / 405

Si ves 405 Method Not Allowed en OPTIONS /fit_ok, habilita CORS para POST y evita preflights vacíos desde el cliente. El frontend debe llamar directamente con POST (sin OPTIONS manual).
