import os
from tensorflow.keras.preprocessing.image import ImageDataGenerator, img_to_array, load_img, array_to_img, save_img
import random

# Carpeta de defectuosos (ajusta si tienes subcarpetas)
defective_dir = 'data/train/defective'
output_count = 5  # Número de augmentaciones por imagen original (ajusta según lo que necesites)

datagen = ImageDataGenerator(
    rotation_range=30,                # Rotación hasta ±30º
    width_shift_range=0.15,           # Desplazamiento horizontal 15%
    height_shift_range=0.15,          # Desplazamiento vertical 15%
    shear_range=0.15,                 # Shear (corte) hasta 15%
    zoom_range=0.15,                  # Zoom in/out hasta 15%
    brightness_range=(0.85, 1.15),    # Brillo entre 85% y 115%
    horizontal_flip=True,
    vertical_flip=True,
    fill_mode='nearest'
)

# Procesa cada imagen defectuosa
files = [f for f in os.listdir(defective_dir) if f.lower().endswith(('.jpg', '.jpeg', '.png'))]
for img_name in files:
    img_path = os.path.join(defective_dir, img_name)
    img = load_img(img_path)
    x = img_to_array(img)
    x = x.reshape((1,) + x.shape)

    # Genera N augmentaciones por imagen
    i = 0
    for batch in datagen.flow(x, batch_size=1):
        aug_name = f"aug_{random.randint(1000,9999)}_{img_name.split('.')[0]}.jpg"
        save_img(os.path.join(defective_dir, aug_name), array_to_img(batch[0]))
        i += 1
        if i >= output_count:
            break

print("¡Augmentation completado para la clase 'defective'!")
