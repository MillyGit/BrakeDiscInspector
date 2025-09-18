import numpy as np
from PIL import Image
import matplotlib.pyplot as plt

from train_model import load_preprocessing_config, apply_custom_preprocessing

# Carga la config
pre_cfg = load_preprocessing_config("preprocessing_config.txt")

# Carga una imagen
img_path = "data/train/good/Imagen100002.BMP"  # Usa el path de tu imagen
img_arr = np.array(Image.open(img_path).convert('RGB'))

# Aplica el preprocesado
img_proc = apply_custom_preprocessing(img_arr, pre_cfg)

# Guarda la imagen preprocesada como PNG
Image.fromarray(img_proc.astype('uint8')).save("preprocessed_python.png")
print("Imagen preprocesada guardada como preprocessed_python.png")
