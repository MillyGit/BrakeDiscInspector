"""Utility helpers for backend status endpoints.

These functions provide metadata aggregation for model artifacts and logs
so the REST endpoints can expose richer diagnostics without duplicating
filesystem logic across modules.
"""
from __future__ import annotations

import math
import os
from pathlib import Path
from typing import Any, Iterable, Optional


def file_metadata(path: os.PathLike[str] | str) -> dict[str, Any]:
    """Return basic metadata for *path*.

    The dictionary always contains ``path`` (absolute string), ``exists``
    (bool), ``size_bytes`` (int or ``None``) and ``modified_at`` (Unix
    timestamp as float or ``None``). Errors while reading metadata are
    swallowed so the caller always receives a JSON-serialisable payload.
    """
    p = Path(path)
    info: dict[str, Any] = {
        "path": str(p),
        "exists": p.exists(),
        "size_bytes": None,
        "modified_at": None,
    }
    if not info["exists"]:
        return info
    try:
        stat = p.stat()
    except OSError:
        return info
    info["size_bytes"] = int(stat.st_size)
    info["modified_at"] = float(stat.st_mtime)
    return info


def tail_file(path: os.PathLike[str] | str, max_bytes: int = 4096) -> Optional[str]:
    """Read the trailing ``max_bytes`` of *path*.

    ``None`` is returned when the file does not exist. When ``max_bytes`` is
    zero or negative an empty string is returned. The function reads from the
    end of the file without loading the full content into memory when the
    file is large.
    """
    if max_bytes <= 0:
        return ""
    p = Path(path)
    if not p.exists() or not p.is_file():
        return None
    try:
        with p.open("rb") as fh:
            fh.seek(0, os.SEEK_END)
            size = fh.tell()
            offset = max(0, size - max_bytes)
            fh.seek(offset, os.SEEK_SET)
            data = fh.read()
    except OSError:
        return None
    return data.decode("utf-8", errors="ignore")


def _shape_to_list(shape: Any) -> Any:
    if shape is None:
        return None
    if isinstance(shape, (list, tuple)):
        return [_shape_to_list(s) for s in shape]
    if hasattr(shape, "as_list"):
        try:
            return [None if dim is None else int(dim) for dim in shape.as_list()]
        except (TypeError, ValueError):
            pass
    try:
        return [None if dim is None else int(dim) for dim in shape]  # type: ignore[arg-type]
    except TypeError:
        if isinstance(shape, (int, float)):
            return int(shape)
        return str(shape)


def _count_params(weights: Iterable[Any]) -> int:
    total = 0
    for w in weights or []:
        shape = getattr(w, "shape", None)
        if shape is None:
            continue
        if hasattr(shape, "as_list"):
            dims = shape.as_list()
        else:
            try:
                dims = list(shape)  # type: ignore[arg-type]
            except TypeError:
                continue
        if not dims:
            continue
        numeric_dims = []
        for dim in dims:
            if dim is None:
                numeric_dims = []
                break
            numeric_dims.append(int(dim))
        if not numeric_dims:
            continue
        total += int(math.prod(numeric_dims))
    return total


def describe_keras_model(model: Any) -> dict[str, Any]:
    """Return a JSON friendly summary for a Keras model instance."""
    layers = list(getattr(model, "layers", []) or [])
    preview: list[str] = []
    for layer in layers[:5]:
        name = getattr(layer, "name", None)
        if name:
            preview.append(str(name))
    if len(layers) > 5:
        preview.append("...")

    info: dict[str, Any] = {
        "loaded": True,
        "name": getattr(model, "name", None),
        "layer_count": len(layers),
        "layers_preview": preview,
        "input_shape": _shape_to_list(getattr(model, "input_shape", None)),
        "output_shape": _shape_to_list(getattr(model, "output_shape", None)),
        "dtype": getattr(model, "dtype", None),
        "trainable_params": _count_params(getattr(model, "trainable_weights", [])),
        "non_trainable_params": _count_params(getattr(model, "non_trainable_weights", [])),
    }
    return info


def read_threshold(path: os.PathLike[str] | str) -> Optional[float]:
    """Return the float stored in ``path`` or ``None`` on failure."""
    p = Path(path)
    if not p.exists():
        return None
    try:
        text = p.read_text(encoding="utf-8").strip()
        if not text:
            return None
        return float(text)
    except (OSError, ValueError):
        return None
