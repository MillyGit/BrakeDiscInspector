from __future__ import annotations
import numpy as np
from typing import Optional

def choose_threshold(ok_scores: np.ndarray, ng_scores: Optional[np.ndarray]=None, percentile: int = 99) -> float:
    """
    Devuelve un umbral sugerido a partir de:
      - p{percentile} de los OK (por defecto p99),
      - y opcionalmente p5 de los NG si existen (para separar).
    Si p5(NG) <= p{percentile}(OK), añade un pequeño margen al OK para evitar solapes.
    """
    ok_scores = np.asarray(ok_scores, dtype=float)
    if ok_scores.size == 0:
        raise ValueError("Se requiere al menos 1 score OK para calibrar")

    p_ok = float(np.percentile(ok_scores, percentile))

    if ng_scores is None or len(ng_scores) == 0:
        return p_ok

    ng_scores = np.asarray(ng_scores, dtype=float)
    p_ng = float(np.percentile(ng_scores, 5))

    if p_ng <= p_ok:
        return p_ok * 1.02  # pequeño margen

    return (p_ok + p_ng) * 0.5
