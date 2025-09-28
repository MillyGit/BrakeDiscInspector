import io
import sys
import types
from pathlib import Path
from typing import Callable

import numpy as np
from PIL import Image


def _png_bytes(color: int = 128) -> bytes:
    buf = io.BytesIO()
    Image.new("RGB", (10, 10), color=(color, color, color)).save(buf, format="PNG")
    return buf.getvalue()


def _setup_app(monkeypatch, score_provider: Callable[[], float]):
    repo_root = Path(__file__).resolve().parents[2]
    backend_dir = repo_root / "backend"
    if str(backend_dir) not in sys.path:
        sys.path.insert(0, str(backend_dir))

    if "tensorflow" not in sys.modules:
        tf_stub = types.ModuleType("tensorflow")
        keras_stub = types.SimpleNamespace(
            mixed_precision=types.SimpleNamespace(set_global_policy=lambda *args, **kwargs: None),
            models=types.SimpleNamespace(load_model=lambda *args, **kwargs: object()),
        )
        tf_stub.keras = keras_stub
        sys.modules["tensorflow"] = tf_stub

    from backend import app as app_mod

    class DummyModel:
        def predict(self, batch, verbose=0):
            return np.full((batch.shape[0],), score_provider(), dtype=np.float32)

    def fake_ensure_model():
        fake_ensure_model.model = DummyModel()
        fake_ensure_model.thr = 0.5
        return True, "ok"

    fake_ensure_model.model = DummyModel()
    fake_ensure_model.thr = 0.5

    monkeypatch.setattr(app_mod, "_ensure_model", fake_ensure_model)
    monkeypatch.setattr(app_mod, "preprocess_for_model", lambda *args, **kwargs: np.zeros((600, 600, 3), dtype=np.float32))
    monkeypatch.setattr(app_mod, "_load_pre_cfg_from_request", lambda: {})
    monkeypatch.setattr(app_mod, "_generate_heatmap_b64", lambda *args, **kwargs: "heatmap")

    return app_mod.app


def test_predict_and_analyze_label_consistency(monkeypatch):
    scores = {"value": 0.0}

    def score_provider():
        return scores["value"]

    app = _setup_app(monkeypatch, score_provider)
    client = app.test_client()
    payload_bytes = _png_bytes()

    def call_predict() -> str:
        data = {"image": (io.BytesIO(payload_bytes), "disc.png")}
        resp = client.post("/predict", data=data, content_type="multipart/form-data")
        assert resp.status_code == 200, resp.get_data(as_text=True)
        body = resp.get_json()
        assert isinstance(body, list) and body, "predict should return list with entries"
        status = body[0].get("status")
        assert status in {"good", "defective"}
        return status

    def call_analyze() -> str:
        data = {"file": (io.BytesIO(payload_bytes), "disc.png")}
        resp = client.post("/analyze", data=data, content_type="multipart/form-data")
        assert resp.status_code == 200, resp.get_data(as_text=True)
        body = resp.get_json()
        label = body.get("label")
        assert label in {"OK", "NG", "good", "defective"}
        return label

    scores["value"] = 0.8
    status_high = call_predict()
    label_high = call_analyze()
    assert status_high == "good"
    assert label_high.lower() in {"ok", "good"}

    scores["value"] = 0.2
    status_low = call_predict()
    label_low = call_analyze()
    assert status_low == "defective"
    assert label_low.lower() in {"ng", "defective"}
