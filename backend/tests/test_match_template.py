import numpy as np
import cv2

def test_match_template_basic():
    from backend.app import _match_template, _build_mask_from_tpl
    img = np.zeros((200, 200), np.uint8)
    cv2.rectangle(img, (80, 90), (120, 130), 255, -1)

    tpl = np.zeros((40, 40), np.uint8)
    cv2.rectangle(tpl, (0, 0), (40, 40), 255, -1)

    mask = _build_mask_from_tpl(tpl)
    min_val, max_val, min_loc, max_loc, res = _match_template(img, tpl, mask)
    # esperamos un match alto y cerca de (80,90)
    assert max_val > 0.9
    assert abs(max_loc[0] - 80) <= 2
    assert abs(max_loc[1] - 90) <= 2
