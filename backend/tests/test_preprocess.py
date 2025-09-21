import numpy as np
import cv2

def test_letterbox_like_aspect_ratio():
    from backend.preprocess import letterbox
    img = np.full((100, 200, 3), 128, np.uint8)  # 2:1
    out, ratio, dwdh = letterbox(img, new_shape=(224, 224))
    assert out.shape[:2] == (224, 224)
    assert 0.9 < ratio[0] <= 1.0  # no deforma en exceso
    assert 0 <= dwdh[0] <= 224 and 0 <= dwdh[1] <= 224
