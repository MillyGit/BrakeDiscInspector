import sys
import os
import numpy as np
from PIL import Image
import matplotlib.pyplot as plt

def load_preprocessing_config(path="preprocessing_config.txt"):
    pre_cfg = {}
    if not os.path.exists(path):
        print(f"[ERROR] No se encontró el archivo de configuración de preprocesado: {path}")
        sys.exit(1)
    try:
        with open(path, encoding="utf-8") as f:
            for line in f:
                if '=' in line:
                    # divide solo en la primera ocurrencia
                    key, value = line.strip().split('=', 1)
                    key = key.strip()
                    # permite comentarios con "#"
                    value = value.split('#', 1)[0].strip()
                    if not value:
                        continue
                    try:
                        pre_cfg[key] = float(value)
                    except ValueError:
                        pre_cfg[key] = value
        # Chequeo de claves importantes
        required_keys = ["Normalization", "Brightness", "Contrast", "Gamma", "ClipMin", "ClipMax", "Blur"]
        for rk in required_keys:
            if rk not in pre_cfg:
                print(f"[ERROR] Falta la clave '{rk}' en el archivo de configuración.")
                sys.exit(2)
    except Exception as e:
        print(f"[ERROR] Error al leer el archivo de configuración: {e}")
        sys.exit(3)

    print("\n===== PRESET DE PREPROCESADO LEÍDO =====")
    for k, v in pre_cfg.items():
        print(f"  {k}: {v}")
    print("========================================\n")
    return pre_cfg

# Ejemplo de función de preprocesado en numpy + PIL
def apply_custom_preprocessing(img, pre_cfg):
    # img es un numpy array tipo uint8 [H,W,3] rango [0,255]
    arr = img.astype(np.float32)

    # Brillo
    brightness = float(pre_cfg["Brightness"])
    arr = arr + brightness * 25

    # Contraste
    contrast = float(pre_cfg["Contrast"])
    arr = (arr - 127.5) * contrast + 127.5

    # Gamma
    gamma = float(pre_cfg["Gamma"])
    arr = 255.0 * ((arr / 255.0) ** gamma)

    # Clipping
    min_v, max_v = float(pre_cfg["ClipMin"]), float(pre_cfg["ClipMax"])
    arr = np.clip(arr, min_v, max_v)

    # Normalización visual (para guardado / preview)
    arr = 255 * (arr - arr.min()) / (arr.max() - arr.min() + 1e-8)
    arr = np.clip(arr, 0, 255).astype(np.uint8)

    return arr

def mostrar_imagen_preprocesada(data_dir, pre_cfg):
    # Busca una imagen de ejemplo
    for class_folder in os.listdir(data_dir):
        folder = os.path.join(data_dir, class_folder)
        if os.path.isdir(folder):
            imgs = [f for f in os.listdir(folder) if f.lower().endswith(('.jpg','.jpeg','.bmp','.png'))]
            if imgs:
                ejemplo_path = os.path.join(folder, imgs[0])
                print(f"Ejemplo mostrado: {ejemplo_path}")
                img = np.array(Image.open(ejemplo_path).convert("RGB"))
                proc = apply_custom_preprocessing(img, pre_cfg)

                # Mostrar y guardar
                plt.figure(figsize=(8,4))
                plt.subplot(1,2,1); plt.imshow(img); plt.title("Original"); plt.axis("off")
                plt.subplot(1,2,2); plt.imshow(proc); plt.title("Preprocesada"); plt.axis("off")
                plt.tight_layout()
                plt.savefig("preset_preview.png")
                print("[OK] Imagen de muestra guardada como preset_preview.png")
                # Descomenta si quieres ver la ventana interactiva:
                # plt.show()
                plt.close()
                return
    print("[ADVERTENCIA] No se encontró imagen de ejemplo en el dataset.")

# USO EN TU SCRIPT PRINCIPAL
DATA_DIR = "data/train"
pre_cfg = load_preprocessing_config(
    "/home/millylinux/BrakeDiscDefectDetection/BrakeDiscDefectDetection/brake_disc_model/preprocessing_config.txt"
)
mostrar_imagen_preprocesada(DATA_DIR, pre_cfg)
