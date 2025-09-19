import sys
from pathlib import Path

sys.path.append(str(Path(__file__).resolve().parents[1]))

from status_utils import file_metadata, tail_file, describe_keras_model, read_threshold


class FakeWeight:
    def __init__(self, shape):
        self.shape = shape


class FakeLayer:
    def __init__(self, name):
        self.name = name


class FakeModel:
    def __init__(self):
        self.name = "fake"
        self.layers = [FakeLayer(f"layer_{i}") for i in range(6)]
        self.input_shape = (None, 10)
        self.output_shape = (None, 2)
        self.dtype = "float32"
        self.trainable_weights = [FakeWeight((10, 2)), FakeWeight((2,))]
        self.non_trainable_weights = [FakeWeight((2,))]


def test_file_metadata(tmp_path):
    file_path = tmp_path / "model.h5"
    file_path.write_bytes(b"12345")

    info = file_metadata(file_path)
    assert info["exists"] is True
    assert info["size_bytes"] == 5
    assert info["path"].endswith("model.h5")
    assert isinstance(info["modified_at"], float)

    missing = file_metadata(tmp_path / "missing.txt")
    assert missing["exists"] is False
    assert missing["size_bytes"] is None


def test_tail_file(tmp_path):
    file_path = tmp_path / "log.txt"
    file_path.write_text("line1\nline2\nline3\n", encoding="utf-8")

    tail = tail_file(file_path, max_bytes=6)
    assert tail.endswith("line3\n")

    assert tail_file(tmp_path / "nope.log") is None
    assert tail_file(file_path, max_bytes=0) == ""


def test_describe_keras_model():
    model = FakeModel()
    info = describe_keras_model(model)

    assert info["loaded"] is True
    assert info["name"] == "fake"
    assert info["layer_count"] == 6
    assert info["layers_preview"] == ["layer_0", "layer_1", "layer_2", "layer_3", "layer_4", "..."]
    assert info["input_shape"] == [None, 10]
    assert info["output_shape"] == [None, 2]
    assert info["trainable_params"] == 22
    assert info["non_trainable_params"] == 2


def test_read_threshold(tmp_path):
    thr = tmp_path / "threshold.txt"
    thr.write_text("0.75", encoding="utf-8")
    assert read_threshold(thr) == 0.75

    thr.write_text("", encoding="utf-8")
    assert read_threshold(thr) is None

    assert read_threshold(tmp_path / "missing.txt") is None
