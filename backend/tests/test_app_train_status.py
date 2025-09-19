
import contextlib
import importlib.util
import json
import sys
import tempfile
import types
from pathlib import Path
from unittest import mock

BACKEND_DIR = Path(__file__).resolve().parents[1]


def _load_backend_module(name: str):
    module_path = BACKEND_DIR / f"{name}.py"
    if name in sys.modules:
        sys.modules.pop(name)
    spec = importlib.util.spec_from_file_location(name, module_path)
    if spec is None or spec.loader is None:
        raise ImportError(f"Cannot load module {name} from {module_path}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module

try:
    import cv2  # noqa: F401
except Exception:
    sys.modules.pop("cv2", None)

    def _missing_cv2(*args, **kwargs):
        raise RuntimeError("OpenCV is not available in the test environment")

    cv2_stub = types.ModuleType("cv2")
    cv2_stub.COLOR_BGR2GRAY = 0
    cv2_stub.MORPH_ELLIPSE = 0
    cv2_stub.MORPH_ERODE = 0
    cv2_stub.MARKER_CROSS = 0
    cv2_stub.TM_CCORR_NORMED = 0
    cv2_stub.TM_CCOEFF_NORMED = 0
    cv2_stub.NORM_MINMAX = 0
    cv2_stub.COLORMAP_JET = 0
    cv2_stub.INTER_NEAREST = 0
    cv2_stub.INTER_LINEAR = 0

    for name in (
        "imencode",
        "cvtColor",
        "split",
        "normalize",
        "applyColorMap",
        "merge",
        "threshold",
        "getStructuringElement",
        "morphologyEx",
        "matchTemplate",
        "polylines",
        "drawMarker",
        "imwrite",
    ):
        setattr(cv2_stub, name, _missing_cv2)

    sys.modules["cv2"] = cv2_stub

try:
    import tensorflow  # noqa: F401
except Exception:
    sys.modules.pop("tensorflow", None)

    keras_stub = types.SimpleNamespace(
        mixed_precision=types.SimpleNamespace(set_global_policy=lambda *_, **__: None),
        models=types.SimpleNamespace(load_model=lambda *_, **__: object()),
    )

    tensorflow_stub = types.ModuleType("tensorflow")
    tensorflow_stub.keras = keras_stub
    sys.modules["tensorflow"] = tensorflow_stub


_load_backend_module("preprocess")
_load_backend_module("status_utils")
app_module = _load_backend_module("app")


@contextlib.contextmanager
def app_test_client():
    with tempfile.TemporaryDirectory() as tmpdir:
        root = Path(tmpdir)
        model_dir = root / "model"
        logs_dir = model_dir / "logs"
        model_dir.mkdir(parents=True)
        logs_dir.mkdir()

        patchers = [
            mock.patch.object(app_module, "MODEL_DIR", model_dir),
            mock.patch.object(app_module, "LOGS_DIR", logs_dir),
            mock.patch.object(app_module, "MODEL_PATH", model_dir / "current_model.h5"),
            mock.patch.object(app_module, "THR_PATH", model_dir / "threshold.txt"),
            mock.patch.object(app_module, "DBG_IMG", model_dir / "last_match_debug.png"),
        ]

        with contextlib.ExitStack() as stack:
            for patcher in patchers:
                stack.enter_context(patcher)
            stack.enter_context(
                mock.patch.object(app_module, "_ensure_model", lambda: (False, "not loaded"))
            )
            client = stack.enter_context(app_module.app.test_client())
            yield client, model_dir


def test_train_status_uses_computed_artifacts():
    with app_test_client() as (client, model_dir):
        train_status_file = model_dir / "train_status.json"
        payload = {
            "state": "running",
            "artifacts": {"train_status": {"custom": "value"}},
        }
        train_status_file.write_text(json.dumps(payload), encoding="utf-8")

        response = client.get("/train_status")
        assert response.status_code == 200

        data = response.get_json()
        assert data["state"] == "running"

        artifacts = data["artifacts"]
        assert {"model", "threshold", "train_status", "pid_file", "log"}.issubset(
            artifacts.keys()
        )
        assert artifacts["train_status"]["exists"] is True

