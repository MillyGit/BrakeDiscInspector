# Docker (GPU) en Windows

## Requisitos
- Windows 10/11 con **WSL2** y **Docker Desktop**.
- Drivers NVIDIA + **NVIDIA Container Toolkit**.
- Base: `pytorch/pytorch:2.2.2-cuda12.1-cudnn8-runtime`.

## Construir
```bash
docker build -t brakedisc-backend -f docker/Dockerfile .
```

## Ejecutar con GPU
```bash
docker run --rm -it --gpus all -p 8000:8000 brakedisc-backend
```

## Probar
```bash
curl http://127.0.0.1:8000/health
```
Debe devolver `device: "cuda"` si la GPU está accesible.
