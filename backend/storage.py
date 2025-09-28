from __future__ import annotations
import numpy as np
from pathlib import Path
from typing import Tuple, Optional, Dict, Any
import json
from .utils import ensure_dir, save_json, load_json

class ModelStore:
    def __init__(self, root: Path):
        self.root = Path(root)

    def _dir(self, role_id: str, roi_id: str) -> Path:
        return self.root / role_id / roi_id

    def save_memory(
        self,
        role_id: str,
        roi_id: str,
        embeddings: np.ndarray,
        token_hw: Tuple[int, int],
        metadata: Optional[Dict[str, Any]] = None,
    ):
        """
        Guarda la memoria (embeddings coreset L2-normalizados) y la forma del grid de tokens.
        """
        d = self._dir(role_id, roi_id)
        ensure_dir(d)
        payload = {
            "emb": embeddings.astype(np.float32),
            "token_h": int(token_hw[0]),
            "token_w": int(token_hw[1]),
        }
        if metadata:
            payload["metadata"] = json.dumps(metadata)
        np.savez_compressed(d / "memory.npz", **payload)

    def load_memory(self, role_id: str, roi_id: str):
        """
        Carga (embeddings, (Ht, Wt)) o None si no existe.
        """
        p = self._dir(role_id, roi_id) / "memory.npz"
        if not p.exists():
            return None
        with np.load(p, allow_pickle=False) as z:
            emb = z["emb"].astype(np.float32)
            H = int(z["token_h"])
            W = int(z["token_w"])
            metadata = {}
            if "metadata" in z.files:
                meta_raw = z["metadata"]
                if np.ndim(meta_raw) == 0:
                    meta_str = str(meta_raw.item())
                else:
                    meta_str = str(meta_raw)
                try:
                    metadata = json.loads(meta_str)
                except Exception:
                    metadata = {}
        return emb, (H, W), metadata

    def save_index_blob(self, role_id: str, roi_id: str, blob: bytes):
        d = self._dir(role_id, roi_id)
        ensure_dir(d)
        (d / "index.faiss").write_bytes(blob)

    def load_index_blob(self, role_id: str, roi_id: str) -> Optional[bytes]:
        p = self._dir(role_id, roi_id) / "index.faiss"
        if p.exists():
            return p.read_bytes()
        return None

    def save_calib(self, role_id: str, roi_id: str, data: dict):
        p = self._dir(role_id, roi_id) / "calib.json"
        save_json(p, data)

    def load_calib(self, role_id: str, roi_id: str, default=None):
        p = self._dir(role_id, roi_id) / "calib.json"
        return load_json(p, default=default)
