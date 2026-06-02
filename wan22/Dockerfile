# Wan 2.2 TI2V-5B inference container for Azure Container Apps Serverless GPU.
# Target SKU: Consumption-GPU-NC24-A100 (80GB VRAM, plenty of headroom for 5B model).
# Build: az acr build --registry <ACR> --image wan22:v1 .
#
# NOTE: weights (~28GB) are baked into the image to avoid cold-start downloads.
# Image size will be ~32GB. Pull is a one-time cost; kept warm by min-replicas=0
# scale-up only when /infer is hit.

FROM nvidia/cuda:12.4.1-cudnn-runtime-ubuntu22.04

ENV DEBIAN_FRONTEND=noninteractive \
    PYTHONUNBUFFERED=1 \
    HF_HOME=/weights/.cache \
    PIP_NO_CACHE_DIR=1

RUN apt-get update && apt-get install -y --no-install-recommends \
        python3.11 python3-pip git ffmpeg ca-certificates \
    && rm -rf /var/lib/apt/lists/*

RUN ln -sf /usr/bin/python3.11 /usr/bin/python

RUN pip install --upgrade pip && \
    pip install torch==2.5.1 torchvision==0.20.1 \
        --index-url https://download.pytorch.org/whl/cu124

RUN pip install \
        diffusers==0.32.0 \
        transformers==4.46.3 \
        accelerate==1.1.1 \
        safetensors==0.4.5 \
        huggingface_hub==0.26.2 \
        sentencepiece==0.2.0 \
        ftfy==6.3.1 \
        imageio[ffmpeg]==2.36.0 \
        fastapi==0.115.5 \
        uvicorn[standard]==0.32.1 \
        pydantic==2.10.2 \
        azure-storage-blob==12.24.0 \
        azure-identity==1.19.0

# Bake weights. Comment out + mount via Azure Files for faster image push.
RUN python -c "from huggingface_hub import snapshot_download; \
    snapshot_download('Wan-AI/Wan2.2-TI2V-5B-Diffusers', local_dir='/weights/wan22', \
                      max_workers=4, allow_patterns=['*.json','*.safetensors','*.txt','*.model'])"

WORKDIR /app
COPY server.py /app/server.py

EXPOSE 8080
CMD ["uvicorn", "server:app", "--host", "0.0.0.0", "--port", "8080", "--workers", "1"]
