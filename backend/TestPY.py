import os
# Establecer nivel de log ANTES de importar TensorFlow
os.environ['TF_CPP_MIN_LOG_LEVEL'] = '2'  # 0=all, 1=info, 2=warnings, 3=errors
import tensorflow as tf

print("\n=== GPU Verification ===")
gpu_devices = tf.config.list_physical_devices('GPU')
print("GPU devices found:", gpu_devices)
if gpu_devices:
    print("CUDA Version:", tf.sysconfig.get_build_info()["cuda_version"])
    print("cuDNN Version:", tf.sysconfig.get_build_info()["cudnn_version"])
else:
    print("No GPU disponible.")
