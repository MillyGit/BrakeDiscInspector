# GPU-enabled base image with PyTorch + CUDA 12.1 runtime
FROM pytorch/pytorch:2.2.2-cuda12.1-cudnn8-runtime

WORKDIR /app

# Copy backend requirements and install (avoid reinstalling torch)
COPY backend/requirements.txt /app/backend/requirements.txt
# Remove CPU-only extra index if present (we already have CUDA torch)
RUN sed -i '/pytorch.org\\/whl\\/cpu/d' /app/backend/requirements.txt || true

RUN pip install --no-cache-dir -r /app/backend/requirements.txt

# Copy the backend code
COPY backend /app/backend

EXPOSE 8000

# Enable cuDNN autotune for variable input sizes
ENV CUDNN_BENCHMARK=1

CMD ["python", "-m", "uvicorn", "backend.app:app", "--host", "0.0.0.0", "--port", "8000"]
