import base64
import io
import numpy as np
import cv2
import types

def _encode_png(npimg):
    ok, buf = cv2.imencode('.png', npimg)
    assert ok
    return base64.b64encode(buf.tobytes()).decode("ascii")

def test_predict_smoke(monkeypatch):
    # Importa app y prepara un modelo falso
    from backend import app as app_mod
    app = app_mod.app

    class DummyModel:
        def predict(self, x, verbose=0):
            # x = (N, H, W, C), devolvemos prob alta si hay un blob blanco
            batch = x.shape[0]
            # regla tonta: media > 0.5 => "defect"
            p = (x.mean(axis=(1,2,3)) > 0.5).astype(np.float32)
            return np.stack([1 - p, p], axis=1)

    # Inyecta modelo y umbral
    app_mod.MODEL = DummyModel()
    app_mod.THRESHOLD = 0.5
    # Marca ensure_model como inicializado
    app_mod._ensure_model.initialized = True

    # Crea imagen con “defecto”
    img = np.zeros((128, 128, 3), np.uint8)
    cv2.circle(img, (64, 64), 20, (255,255,255), -1)
    b64 = _encode_png(img)

    client = app.test_client()
    payload = {"image": b64, "debug": True}
    resp = client.post("/predict", json=payload)
    assert resp.status_code == 200, resp.get_data(as_text=True)
    data = resp.get_json()
    assert "label" in data and "score" in data and "threshold" in data
