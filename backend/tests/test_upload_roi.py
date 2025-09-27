import io
import importlib.util
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
        "Laplacian",
        "GaussianBlur",
        "bitwise_and",
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


def _not_implemented(*args, **kwargs):  # pragma: no cover - placeholder stub
    raise NotImplementedError


if "preprocess" not in sys.modules:
    preprocess_stub = types.ModuleType("preprocess")
    preprocess_stub.preprocess_for_model = _not_implemented
    preprocess_stub.load_preprocessing_config = _not_implemented
    sys.modules["preprocess"] = preprocess_stub

if "status_utils" not in sys.modules:
    status_utils_stub = types.ModuleType("status_utils")

    for name in ("file_metadata", "tail_file", "describe_keras_model", "read_threshold"):
        setattr(status_utils_stub, name, _not_implemented)

    sys.modules["status_utils"] = status_utils_stub

if "PIL" not in sys.modules:
    pil_module = types.ModuleType("PIL")
    image_stub = types.ModuleType("PIL.Image")
    image_stub.open = _not_implemented
    pil_module.Image = image_stub
    sys.modules["PIL"] = pil_module
    sys.modules["PIL.Image"] = image_stub

app_module = _load_backend_module("app")


def test_upload_roi_rejects_malicious_filename():
    with tempfile.TemporaryDirectory() as tmpdir:
        data_dir = Path(tmpdir)
        malicious_name = "../../../../etc/passwd"
        with mock.patch.object(app_module, "DATA_DIR", data_dir):
            client = app_module.app.test_client()
            resp = client.post(
                "/upload_roi",
                data={
                    "label": "good",
                    "image": (io.BytesIO(b"fake-image"), malicious_name),
                },
                content_type="multipart/form-data",
            )

        assert resp.status_code == 200, resp.get_data(as_text=True)
        payload = resp.get_json()
        saved_path = Path(payload["saved"]).resolve()
        target_dir = (data_dir / "good").resolve()

        assert saved_path.is_file()
        assert saved_path.suffix == ".png"
        assert saved_path.parent == target_dir
