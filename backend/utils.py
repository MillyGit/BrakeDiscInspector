# utils.py
import json
import logging
import h5py
import tensorflow as tf
from tensorflow.keras.models import load_model  # type: ignore


def dump_model_config(model_path: str):
    try:
        logging.info(f"Abriendo modelo para volcar config: {model_path}")
        with h5py.File(model_path, 'r') as f:
            cfg_bytes = f.attrs.get('model_config')
            if cfg_bytes is None:
                logging.warning("No se encontró 'model_config' en HDF5.")
                return
            # Decode JSON, handling both bytes and str
            if isinstance(cfg_bytes, bytes):
                cfg = json.loads(cfg_bytes.decode('utf-8'))
            else:
                cfg = json.loads(cfg_bytes)
            layer_names = [layer['class_name'] for layer in cfg['config']['layers']]
            logging.info(f"Capas en config: {layer_names}")
    except Exception as e:
        logging.error(f"Error volcando config: {e}")


def load_model_with_logging(model_path, custom_objects):
    """Carga modelo con logging y dump en caso de fallo, incluyendo capas personalizadas."""
    # Define a Cast layer stub matching serialized signature
    class Cast(tf.keras.layers.Layer):  # type: ignore
        def __init__(self, dtype, **kwargs):
            super().__init__(**kwargs)
            self.cast_dtype = dtype
        def call(self, inputs):
            return tf.cast(inputs, self.cast_dtype)
        def get_config(self):
            base = super().get_config()
            base.update({'dtype': self.cast_dtype})
            return base

    # Merge in custom_objects, registering F1Score, Lambda, and Cast
    all_custom_objects = {**custom_objects, 'Cast': Cast, 'Lambda': tf.keras.layers.Lambda}

    logging.info("Cargando modelo: %s con objetos: %s", model_path, list(all_custom_objects.keys()))
    try:
        model = load_model(model_path, custom_objects=all_custom_objects)
        logging.info("Modelo cargado correctamente.")
        return model
    except ValueError as e:
        logging.critical(f"Falló carga de modelo: {e}")
        dump_model_config(model_path)
        raise

