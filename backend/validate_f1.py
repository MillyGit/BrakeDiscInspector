import tensorflow as tf
import numpy as np
from sklearn.metrics import f1_score as sk_f1, roc_auc_score
import matplotlib.pyplot as plt
import logging
from pathlib import Path

from backend.train_model import PRE_CFG, prepare_data
from backend.utils import load_model_with_logging

BASE_DIR = Path(__file__).resolve().parent
MODEL_DIR = BASE_DIR / "model"
TEST_DATA_DIR = BASE_DIR / "data" / "test"

def validate_model():
    """Validate model performance on validation set"""
    # Load model and threshold
    custom_objects = {
        'Precision': tf.keras.metrics.Precision,
        'Recall': tf.keras.metrics.Recall,
    }

    model = load_model_with_logging(
        MODEL_DIR/'current_model.h5',
        custom_objects=custom_objects
    )
    _, val_gen = prepare_data(pre_cfg=PRE_CFG)
    
    with open(MODEL_DIR / 'threshold.txt', 'r') as f:
        optimal_threshold = float(f.read().strip())
    
    # Collect labels and predictions properly
    y_true = []
    y_pred = []
    for batch in val_gen:
        images, labels = batch
        y_true.extend(labels.numpy())
        y_pred.extend(model.predict(images, verbose=0).flatten())
    
    y_true = np.array(y_true)
    y_pred = np.array(y_pred)
    
    # Calculate metrics
    f1 = sk_f1(y_true, (y_pred > optimal_threshold).astype(int))
    roc_auc = roc_auc_score(y_true, y_pred)
    
    print(f"Validation F1: {f1:.4f}")
    print(f"ROC AUC: {roc_auc:.4f}")
    print(f"Optimal Threshold: {optimal_threshold:.4f}")


def analyze_failures():
    custom_objects = {
        'Precision': tf.keras.metrics.Precision,
        'Recall': tf.keras.metrics.Recall,
    }
    
    model = load_model_with_logging(
        MODEL_DIR/'current_model.h5',
        custom_objects=custom_objects
    )
    _, test_gen = prepare_data(data_dir=TEST_DATA_DIR, pre_cfg=PRE_CFG)

    with open(MODEL_DIR / 'threshold.txt', 'r') as f:
        optimal_threshold = float(f.read().strip())
    
    for batch in test_gen:
        images, labels = batch
        # Undo preprocessing for visualization
        images = (images + 1) * 127.5  # Inverse EfficientNet preprocessing
        preds = model.predict(images, verbose=0)
        
        for i in range(images.shape[0]):
            actual_label = labels[i].numpy()
            pred_label = preds[i][0] > optimal_threshold
            confidence = float(preds[i][0])
            
            if pred_label != actual_label:
                plt.figure(figsize=(8, 4))
                plt.imshow(images[i].numpy().astype(np.uint8))
                plt.title(f"True: {'defective' if actual_label else 'good'}\n"
                          f"Pred: {confidence:.2f} ({'defective' if pred_label else 'good'})")
                plt.axis('off')
                plt.show()

def load_model():
    """Load model helper with diagnostics"""
    custom_objects = {
        'Precision': tf.keras.metrics.Precision,
        'Recall': tf.keras.metrics.Recall,
    }
    
    logging.info("Loading model with custom objects: %s", list(custom_objects.keys()))
    return load_model_with_logging(
        MODEL_DIR/'current_model.h5',
        custom_objects=custom_objects
    )

if __name__ == "__main__":
    validate_model()
    # Uncomment to also run failure analysis
    # analyze_failures()