from __future__ import annotations
import os, json
from pathlib import Path
from typing import Any, Dict

try:
    import yaml  # type: ignore
except Exception:
    yaml = None  # optional dependency

DEFAULTS: Dict[str, Any] = {
    "server": {"host": os.getenv("BRAKEDISC_BACKEND_HOST", "127.0.0.1"),
               "port": int(os.getenv("BRAKEDISC_BACKEND_PORT", "8000"))},
    "models_dir": os.getenv("BRAKEDISC_MODELS_DIR", "models"),
    "inference": {
        "coreset_rate": float(os.getenv("BRAKEDISC_CORESET_RATE", "0.10")),
        "score_percentile": int(os.getenv("BRAKEDISC_SCORE_PERCENTILE", "99")),
        "area_mm2_thr": float(os.getenv("BRAKEDISC_AREA_MM2_THR", "1.0")),
    },
}

def load_settings(config_path: str | os.PathLike[str] | None = None) -> Dict[str, Any]:
    cfg = json.loads(json.dumps(DEFAULTS))  # deep copy via JSON
    path = Path(config_path) if config_path else Path(__file__).resolve().parents[1] / "configs" / "app.yaml"
    if path.exists() and yaml is not None:
        with open(path, "r", encoding="utf-8") as f:
            data = yaml.safe_load(f) or {}
        # shallow merge
        for k, v in data.items():
            if isinstance(v, dict) and isinstance(cfg.get(k), dict):
                cfg[k].update(v)
            else:
                cfg[k] = v
    return cfg
