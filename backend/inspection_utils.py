import cv2
import numpy as np
import os
import csv
import datetime
from PIL import Image
import io

def save_template(img_np, roi_master, output_path):
    cx, cy, size = roi_master["CenterX"], roi_master["CenterY"], roi_master["Size"]
    x1, y1 = int(cx - size // 2), int(cy - size // 2)
    x2, y2 = int(cx + size // 2), int(cy + size // 2)
    template = img_np[y1:y2, x1:x2]
    cv2.imwrite(output_path, cv2.cvtColor(template, cv2.COLOR_RGB2BGR))
    return template

def find_roi_master(image_np, template_np, threshold=0.7):
    result = cv2.matchTemplate(image_np, template_np, cv2.TM_CCOEFF_NORMED)
    min_val, max_val, min_loc, max_loc = cv2.minMaxLoc(result)
    if max_val < threshold:
        return None, max_val
    top_left = max_loc
    h, w = template_np.shape[:2]
    center_x = top_left[0] + w // 2
    center_y = top_left[1] + h // 2
    return (center_x, center_y), max_val

def crop_roi_inspection(image_np, center_x, center_y, roi_inspection):
    offset_x = roi_inspection["OffsetX"]
    offset_y = roi_inspection["OffsetY"]
    size = roi_inspection["Size"]
    x_c = center_x + offset_x
    y_c = center_y + offset_y
    x1, y1 = int(x_c - size // 2), int(y_c - size // 2)
    x2, y2 = int(x_c + size // 2), int(y_c + size // 2)
    return image_np[y1:y2, x1:x2]

def log_result(log_path, img_name, found, match_val, roi_coords):
    header = ["timestamp", "filename", "roi_found", "match_value", "roi_center_x", "roi_center_y"]
    file_exists = os.path.isfile(log_path)
    with open(log_path, 'a', newline='') as f:
        writer = csv.writer(f)
        if not file_exists:
            writer.writerow(header)
        timestamp = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        writer.writerow([timestamp, img_name, int(found), match_val, roi_coords[0], roi_coords[1]])


def extract_template(img_np, roi_master):
    cx, cy, size = roi_master["CenterX"], roi_master["CenterY"], roi_master["Size"]
    roi_type = str(roi_master.get("Type", "Cuadrado")).lower()

    x1, y1 = int(cx - size // 2), int(cy - size // 2)
    x2, y2 = int(cx + size // 2), int(cy + size // 2)

    h, w = img_np.shape[:2]
    x1, y1 = max(0, x1), max(0, y1)
    x2, y2 = min(w, x2), min(h, y2)

    template = img_np[y1:y2, x1:x2].copy()

    if roi_type in ["cuadrado", "square"]:
        return template

    mask = np.zeros(template.shape[:2], dtype=np.uint8)
    center = (template.shape[1] // 2, template.shape[0] // 2)
    radius = min(template.shape[0], template.shape[1]) // 2

    if roi_type in ["círculo", "circle", "circulo"]:
        cv2.circle(mask, center, radius, 255, -1)
    elif roi_type == "annulus":
        cv2.circle(mask, center, radius, 255, -1)
        cv2.circle(mask, center, radius // 2, 0, -1)

    if roi_type in ["círculo", "circle", "circulo", "annulus"]:
        template_masked = cv2.bitwise_and(template, template, mask=mask)
        return template_masked

    return template

def match_roi_with_mask(image_np, roi_master):
    template = extract_template(image_np, roi_master)
    h, w = template.shape[:2]
    roi_type = roi_master.get("Type", "Cuadrado").lower()

    mask = np.zeros((h, w), dtype=np.uint8)
    center = (w // 2, h // 2)
    radius = min(h, w) // 2

    if roi_type in ["cuadrado", "square"]:
        mask[:] = 255
    elif roi_type in ["círculo", "circle", "circulo"]:
        cv2.circle(mask, center, radius, 255, -1)
    elif roi_type == "annulus":
        cv2.circle(mask, center, radius, 255, -1)
        cv2.circle(mask, center, radius // 2, 0, -1)

    res = cv2.matchTemplate(image_np, template, cv2.TM_CCOEFF_NORMED, mask=mask)
    min_val, max_val, min_loc, max_loc = cv2.minMaxLoc(res)
    top_left = max_loc
    match_center = (top_left[0] + w // 2, top_left[1] + h // 2)
    return match_center, max_val

# --- REEMPLAZO ÚNICO ---
def crop_roi_inspection(image_np, center_x, center_y, roi_inspection):
    """
    Recorta la ROI de inspección y la devuelve en formato RGBA (fondo transparente fuera de la forma).
    """
    import cv2
    roi_type = str(roi_inspection.get("Type", "Cuadrado")).lower()
    offset_x = int(roi_inspection.get("OffsetX", 0))
    offset_y = int(roi_inspection.get("OffsetY", 0))
    size     = int(roi_inspection.get("Size", 0))

    # Centro absoluto
    x_c = int(center_x + offset_x)
    y_c = int(center_y + offset_y)
    x1, y1 = int(x_c - size // 2), int(y_c - size // 2)
    x2, y2 = int(x_c + size // 2), int(y_c + size // 2)

    # Límites
    h, w = image_np.shape[:2]
    x1, y1 = max(0, x1), max(0, y1)
    x2, y2 = min(w, x2), min(h, y2)

    roi_crop = image_np[y1:y2, x1:x2].copy()
    if roi_crop.size == 0:
        return np.zeros((1,1,4), dtype=np.uint8)

    # Alpha por defecto opaco
    alpha = np.ones(roi_crop.shape[:2], dtype=np.uint8) * 255

    # Máscaras
    if roi_type in ["círculo","circle","circulo","annulus"]:
        mask = np.zeros(roi_crop.shape[:2], dtype=np.uint8)
        center = (roi_crop.shape[1] // 2, roi_crop.shape[0] // 2)
        radius = min(roi_crop.shape[0], roi_crop.shape[1]) // 2
        cv2.circle(mask, center, radius, 255, -1)
        if roi_type == "annulus":
            cv2.circle(mask, center, radius // 2, 0, -1)
        alpha = mask  # 255 dentro, 0 fuera

    # Asegura RGB -> RGBA
    if roi_crop.ndim == 2:
        roi_crop = cv2.cvtColor(roi_crop, cv2.COLOR_GRAY2RGB)
    if roi_crop.shape[2] == 3:
        roi_rgba = np.dstack([roi_crop, alpha])
    else:
        roi_rgba = roi_crop
    return roi_rgba

