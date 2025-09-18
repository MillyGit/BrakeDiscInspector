import sys
import os
import logging
from pathlib import Path
import argparse
import shutil
import random

import numpy as np
import tensorflow as tf
from tensorflow import keras
from PIL import Image, ImageEnhance, ImageFilter
import matplotlib.pyplot as plt
from sklearn.metrics import roc_curve

# ---- COMPAT KERAS 3 / TF 2.x (silencia Pylance) ----
try:
    # Keras 3 (paquete 'keras' separado)
    from keras.preprocessing.image import ImageDataGenerator  # type: ignore
    from keras.utils import img_to_array, load_img, array_to_img, save_img  # type: ignore
except Exception:
    # Fallback: TF 2.x con keras integrado
    from tensorflow.keras.preprocessing.image import ImageDataGenerator  # type: ignore
    from tensorflow.keras.preprocessing.image import (  # type: ignore
        img_to_array, load_img, array_to_img, save_img
    )

MODEL_DIR = Path('model')
DATA_DIR = Path('data/train')
KERAS_PATH = MODEL_DIR / "best_model_val_loss.h5"
ONNX_PATH  = MODEL_DIR / "best_model_val_loss.onnx"
THRESHOLD_PATH = MODEL_DIR / "best_model_threshold.txt"
PREPROCESSING_CONFIG = "preprocessing_config.txt"  # Ruta relativa

os.environ['TF_CPP_MIN_LOG_LEVEL'] = '2'

logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s %(levelname)s | %(message)s',
    handlers=[
        logging.FileHandler("train_model.log", mode='w', encoding='utf-8'),
        logging.StreamHandler(sys.stdout)
    ]
)

# --- PARCHE: preprocesado unificado (letterbox + [-1,1]) ---
from preprocess import preprocess_for_model, load_preprocessing_config
PRE_CFG = load_preprocessing_config("preprocessing_config.txt")

def load_image_for_training(path: str):
    from PIL import Image
    pil = Image.open(path)
    arr = preprocess_for_model(pil, (600,600), PRE_CFG)   # letterbox + [-1,1]
    return arr  # float32 HxWx3
# ------------------------------------------------------------



policy = tf.keras.mixed_precision.Policy('float32')
tf.keras.mixed_precision.set_global_policy(policy)

def count_images(folder):
    return len([
        f for f in os.listdir(folder)
        if f.lower().endswith(('.png', '.jpg', '.jpeg', '.bmp'))
    ])

def load_preprocessing_config(path=PREPROCESSING_CONFIG):
    pre_cfg = {}
    if not os.path.exists(path):
        print(f"[ERROR] No se encontró el archivo de configuración de preprocesado: {path}")
        sys.exit(1)
    try:
        with open(path, encoding="utf-8") as f:
            for raw in f:
                line = raw.strip()
                if not line or line.startswith("#"):
                    continue
                if "=" not in line:
                    continue
                # divide solo una vez y permite comentarios al final
                key, value = line.split("=", 1)
                key = key.strip()
                value = value.split("#", 1)[0].strip()
                if value == "":
                    continue

                if key in ["Gamma", "Contrast", "Brightness"]:
                    pre_cfg[key] = float(value) / 10.0
                elif key in ["Normalization", "ClipMin", "ClipMax", "Blur"]:
                    pre_cfg[key] = int(float(value))
                else:
                    # Acepta extras sin romper
                    try:
                        pre_cfg[key] = float(value)
                    except ValueError:
                        pre_cfg[key] = value

        required_keys = ["Normalization", "Brightness", "Contrast", "Gamma", "ClipMin", "ClipMax", "Blur"]
        for rk in required_keys:
            if rk not in pre_cfg:
                print(f"[ERROR] Falta la clave '{rk}' en el archivo de configuración.")
                sys.exit(2)
    except Exception as e:
        print(f"[ERROR] Error al leer el archivo de configuración: {e}")
        sys.exit(3)
    print("[OK] Archivo de preprocesado cargado correctamente:", pre_cfg)
    return pre_cfg


def augment_good_images(data_dir, num_aug_needed, pre_cfg, augmentations_per_image=3):
    logging.info(f"Generando {num_aug_needed} imágenes augmentadas en {data_dir}...")
    datagen = ImageDataGenerator(
        rotation_range=30,
        width_shift_range=0.15,
        height_shift_range=0.15,
        shear_range=0.2,
        zoom_range=0.2,
        brightness_range=(max(0.1, 1 + pre_cfg["Brightness"] - 0.5), 1 + pre_cfg["Brightness"] + 0.5),
        horizontal_flip=True,
        vertical_flip=True,
        fill_mode='nearest'
    )

    original_imgs = [
        f for f in os.listdir(data_dir)
        if f.lower().endswith(('.jpg', '.jpeg', '.png', '.bmp')) and not f.startswith("aug_")
    ]
    generated = 0
    while generated < num_aug_needed:
        img_name = random.choice(original_imgs)
        img_path = os.path.join(data_dir, img_name)
        img = load_img(img_path)
        x = img_to_array(img)
        # Opcional: Aplica aquí tu preprocesado custom
        # x = apply_custom_preprocessing(x, pre_cfg)
        x = x.reshape((1,) + x.shape)
        for batch in datagen.flow(x, batch_size=1):
            aug_name = f"aug_{random.randint(1000,9999)}_{img_name}"
            save_img(os.path.join(data_dir, aug_name), array_to_img(batch[0]))
            generated += 1
            if generated >= num_aug_needed:
                break
    logging.info("Augmentation completado para 'good'.")

def apply_custom_preprocessing(img_arr, pre_cfg):
    img = Image.fromarray(img_arr.astype('uint8'), 'RGB')

    # Brillo
    brightness = 1.0 + pre_cfg["Brightness"]
    img = ImageEnhance.Brightness(img).enhance(brightness)

    # Contraste
    contrast = 1.0 + (pre_cfg["Contrast"] - 1.0)
    img = ImageEnhance.Contrast(img).enhance(contrast)

    # Gamma
    gamma = pre_cfg["Gamma"]
    if gamma != 1.0:
        img = np.array(img)
        img = 255.0 * np.power(img / 255.0, gamma)
        img = np.clip(img, 0, 255).astype('uint8')
        img = Image.fromarray(img, 'RGB')

    # Clipping
    clip_min = pre_cfg["ClipMin"]
    clip_max = pre_cfg["ClipMax"]
    img = np.clip(np.array(img), clip_min, clip_max).astype('uint8')
    img = Image.fromarray(img, 'RGB')

    # Blur
    blur = pre_cfg["Blur"]
    if blur > 0:
        img = img.filter(ImageFilter.GaussianBlur(radius=blur))

    # Salida como numpy array float32 (para EfficientNet preprocessing)
    return np.array(img).astype(np.float32)

def preprocess_input_with_custom(x, pre_cfg):
    def np_preproc(img):
        img = apply_custom_preprocessing(img, pre_cfg)
        # Normalización para EfficientNet
        img = img / 127.5 - 1.0
        return img
    x = tf.numpy_function(lambda y: np.stack([np_preproc(img) for img in y]), [x], tf.float32)
    x.set_shape((None, 600, 600, 3))
    return x

def prepare_data(data_dir=DATA_DIR, pre_cfg=None):
    logging.info(f"Cargando datasets desde: {data_dir}")
    train_ds = keras.utils.image_dataset_from_directory(
        data_dir, validation_split=0.15, subset='training', seed=42,
        image_size=(600, 600), batch_size=16, label_mode='binary'
    )
    val_ds = keras.utils.image_dataset_from_directory(
        data_dir, validation_split=0.15, subset='validation', seed=42,
        image_size=(600, 600), batch_size=16, label_mode='binary'
    )

    def map_fn(x, y):
        x = preprocess_input_with_custom(x, pre_cfg)
        return x, y

    train_ds = train_ds.map(map_fn, num_parallel_calls=tf.data.AUTOTUNE)
    val_ds = val_ds.map(map_fn, num_parallel_calls=tf.data.AUTOTUNE)
    logging.info("Datasets cargados y preprocesados correctamente.")
    return train_ds.prefetch(tf.data.AUTOTUNE), val_ds.prefetch(tf.data.AUTOTUNE)

def build_model():
    input_tensor = keras.Input(shape=(600, 600, 3), name="serving_input")
    base_model = keras.applications.EfficientNetB3(
        include_top=False, weights='imagenet', input_tensor=input_tensor
    )
    for layer in base_model.layers[:250]:
        layer.trainable = False
    x = base_model.output
    x = keras.layers.GlobalAveragePooling2D()(x)
    x = keras.layers.Dense(512, activation='relu')(x)
    output = keras.layers.Dense(1, activation='sigmoid', name="serving_output")(x)
    model = keras.Model(inputs=input_tensor, outputs=output)
    return model

def show_example_preprocessed(pre_cfg, data_dir=DATA_DIR):
    """Muestra una imagen de ejemplo ya preprocesada (matplotlib)."""
    # Busca una imagen ejemplo (de good o defective)
    example_dir = None
    for sub in ["good", "defective"]:
        candidate = os.path.join(data_dir, sub)
        if os.path.isdir(candidate) and len(os.listdir(candidate)) > 0:
            example_dir = candidate
            break
    if not example_dir:
        print("[ERROR] No hay imágenes de ejemplo para mostrar.")
        return

    first_img = None
    for f in os.listdir(example_dir):
        if f.lower().endswith(('.jpg', '.jpeg', '.png', '.bmp')):
            first_img = os.path.join(example_dir, f)
            break
    if not first_img:
        print("[ERROR] No hay imágenes válidas para mostrar.")
        return

    arr = np.array(Image.open(first_img).convert("RGB"))
    arr_proc = apply_custom_preprocessing(arr, pre_cfg)

    plt.figure(figsize=(8, 4))
    plt.subplot(1, 2, 1)
    plt.imshow(arr)
    plt.title("Original")
    plt.axis("off")
    plt.subplot(1, 2, 2)
    plt.imshow(np.clip(arr_proc.astype(np.uint8), 0, 255))
    plt.title("Preprocesada")
    plt.axis("off")
    plt.suptitle("Ejemplo de preprocesamiento aplicado")
    plt.tight_layout()
    plt.show()

def train_model(force_fresh=False):
    if force_fresh and MODEL_DIR.exists():
        logging.info("Eliminando modelos anteriores en %s ...", MODEL_DIR)
        shutil.rmtree(MODEL_DIR, ignore_errors=True)
    MODEL_DIR.mkdir(exist_ok=True)

    # ==== PREPROCESSING CONFIG (Corrige aquí la ruta si quieres absoluta o relativa) ====
    pre_cfg = load_preprocessing_config(PREPROCESSING_CONFIG)

    # ==== DATA AUGMENTATION SOLO SI ES NECESARIO ====
    good_dir = os.path.join(DATA_DIR, "good")
    defective_dir = os.path.join(DATA_DIR, "defective")
    n_good = count_images(good_dir)
    n_bad = count_images(defective_dir)
    logging.info(f"Imágenes actuales: good={n_good}, defective={n_bad}")
    if n_good < n_bad:
        num_aug_needed = n_bad - n_good
        logging.info(f"Se generarán {num_aug_needed} imágenes augmentadas para equilibrar la clase 'good'.")
        augment_good_images(good_dir, num_aug_needed, pre_cfg)
        n_good = count_images(good_dir)
        logging.info(f"Ahora la clase 'good' tiene {n_good} imágenes.")
    else:
        logging.info("No es necesario augmentar imágenes: el dataset ya está equilibrado o la clase 'good' es mayor.")

    # ==== DATASETS ====
    train_ds, val_ds = prepare_data(pre_cfg=pre_cfg)

    # ==== MODELO ====
    model = build_model()
    model.compile(optimizer=keras.optimizers.Adam(1e-4),
                  loss='binary_crossentropy',
                  metrics=['accuracy', keras.metrics.Precision(), keras.metrics.Recall()])

    callbacks = [
        keras.callbacks.ModelCheckpoint(
            filepath=KERAS_PATH, save_best_only=True, monitor='val_loss'
        ),
        keras.callbacks.EarlyStopping(patience=5, restore_best_weights=True)
    ]

    logging.info("Comenzando el entrenamiento del modelo...")
    history = model.fit(train_ds, validation_data=val_ds, epochs=100, callbacks=callbacks)
    logging.info("Entrenamiento finalizado.")
    import pandas as pd
    pd.DataFrame(history.history).to_csv("model/training_history.csv", index=False)

    try:
        model.load_weights(KERAS_PATH)
        logging.info("Pesos del best model cargados para exportación.")
        y_true = np.concatenate([y for _, y in val_ds], axis=0)
        y_pred = np.concatenate(model.predict(val_ds))
        fpr, tpr, thresholds = roc_curve(y_true, y_pred)
        optimal_threshold = thresholds[np.argmax(tpr - fpr)]
        with open(THRESHOLD_PATH, 'w') as f:
            f.write(str(optimal_threshold))
        logging.info(f"Threshold óptimo guardado en {THRESHOLD_PATH}: {optimal_threshold:.4f}")

        # Guardar modelo ONNX
        try:
            import tf2onnx
            spec = (tf.TensorSpec((None, 600, 600, 3), tf.float32, name="input"),)
            model_onnx, _ = tf2onnx.convert.from_keras(
                model, input_signature=spec, output_path=str(ONNX_PATH), opset=13
            )
            logging.info(f"Modelo ONNX exportado correctamente en: {ONNX_PATH}")
        except ImportError:
            logging.warning("tf2onnx no está instalado. No se exportó el modelo ONNX.")
        except Exception as e:
            logging.error(f"Error al exportar modelo a ONNX: {e}")

    except Exception as e:
        logging.error(f"Error durante el cálculo del threshold o guardado del modelo: {e}")

    # === Muestra una imagen preprocesada ===
    show_example_preprocessed(pre_cfg, data_dir=DATA_DIR)


    # tras guardar mejor modelo y threshold calculado:
    from pathlib import Path
    import shutil
    MODEL_DIR = Path('model')
    best_h5   = MODEL_DIR / 'best_model_val_loss.h5'   # ajusta si tu script usa otro nombre
    best_thr  = MODEL_DIR / 'best_model_threshold.txt' # idem

    if best_h5.exists():
        shutil.copy2(best_h5, MODEL_DIR / 'current_model.h5')
    if best_thr.exists():
        shutil.copy2(best_thr, MODEL_DIR / 'threshold.txt')
    print("[OK] Exported to model/current_model.h5 and model/threshold.txt")


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--force-fresh', action='store_true',
                        help='Elimina todos los modelos previos y comienza desde cero')
    args = parser.parse_args()
    train_model(force_fresh=args.force_fresh)
    logging.info("Entrenamiento del modelo finalizado.")
    print("Entrenamiento del modelo finalizado.")