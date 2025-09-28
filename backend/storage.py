from __future__ import annotations
import numpy as np
from pathlib import Path
from typing import Tuple, Optional
from .utils import ensure_dir, save_json, load_json

class ModelStore:
    def __init__(self, root: Path):
        self.root = Path(root)

    def _dir(self, role_id: str, roi_id: str) -> Path:
        return self.root / role_id / roi_id

    def save_memory(self, role_id: str, roi_id: str, embeddings: np.ndarray, token_hw: Tuple[int,int]):
        """
        Guarda la memoria (embeddings coreset L2-normalizados) y la forma del grid de tokens.
        """
        d = self._dir(role_id, roi_id)
        ensure_dir(d)
        np.savez_compressed(
            d / "memory.npz",
            emb=embeddings.astype(np.float32),
            token_h=int(token_hw[0]),
            token_w=int(token_hw[1]),
        )

    def load_memory(self, role_id: str, roi_id: str):
        """
        Carga (embeddings, (Ht, Wt)) o None si no existe.
        """
        p = self._dir(role_id, roi_id) / "memory.npz"
        if not p.exists():
            return None
        z = np.load(p, allow_pickle=False)
        emb = z["emb"].astype(np.float32)
        H = int(z["token_h"])
        W = int(z["token_w"])
        return emb, (H, W)

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
