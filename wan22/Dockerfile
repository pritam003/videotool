# ComfyUI + Wan2.2 video wrapper, running on Azure Container Apps A100 Consumption GPU.
# Models are NOT baked into the image — they are mounted at /opt/ComfyUI/models from
# Azure Files (populated once via wan22/download-weights.sh).

FROM pytorch/pytorch:2.5.1-cuda12.4-cudnn9-runtime

ENV DEBIAN_FRONTEND=noninteractive \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    HF_HUB_ENABLE_HF_TRANSFER=1

RUN apt-get update && apt-get install -y --no-install-recommends \
        git ffmpeg libgl1 libglib2.0-0 ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /opt

# ---- ComfyUI core ----
ARG COMFYUI_REF=master
RUN git clone --depth 1 --branch ${COMFYUI_REF} https://github.com/comfyanonymous/ComfyUI.git
WORKDIR /opt/ComfyUI
RUN pip install --upgrade pip && pip install -r requirements.txt

# ---- Wan2.2 wrapper (Kijai) ----
RUN git clone --depth 1 https://github.com/kijai/ComfyUI-WanVideoWrapper.git \
        custom_nodes/ComfyUI-WanVideoWrapper \
 && pip install -r custom_nodes/ComfyUI-WanVideoWrapper/requirements.txt

# ---- Video Helper Suite (provides VHS_VideoCombine -> MP4 output) ----
RUN git clone --depth 1 https://github.com/Kosinkadink/ComfyUI-VideoHelperSuite.git \
        custom_nodes/ComfyUI-VideoHelperSuite \
 && pip install -r custom_nodes/ComfyUI-VideoHelperSuite/requirements.txt

# ---- ComfyUI-Manager (handy for installing more nodes from the UI) ----
RUN git clone --depth 1 https://github.com/ltdrdata/ComfyUI-Manager.git \
        custom_nodes/ComfyUI-Manager

# ---- Helpful runtime extras ----
RUN pip install hf_transfer huggingface_hub safetensors

# Models / output / input come from Azure Files mount (set in container app spec)
RUN mkdir -p models output input

EXPOSE 8188

# --listen 0.0.0.0 so ACA ingress can reach it; --port 8188 default ComfyUI port.
CMD ["python", "main.py", "--listen", "0.0.0.0", "--port", "8188"]
