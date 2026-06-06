#!/usr/bin/env bash
# Populate the Azure Files share with Wan2.2-TI2V-5B weights.
# Runs an Azure Container Instance in the same region (fast in-region download)
# that mounts the share and pulls weights from Hugging Face.
#
# Required env vars (deploy.sh exports these):
#   STORAGE_NAME  - storage account holding the 'models' file share
# Optional:
#   RG, LOC, FILESHARE, HF_TOKEN
set -euo pipefail

RG="${RG:-rg-videotool}"
LOC="${LOC:-eastus}"
FILESHARE="${FILESHARE:-models}"
ACI_NAME="wan22-weight-puller"

if [[ -z "${STORAGE_NAME:-}" ]]; then
  echo "ERROR: STORAGE_NAME is required." >&2
  exit 1
fi

STORAGE_KEY=$(az storage account keys list -g "$RG" -n "$STORAGE_NAME" --query '[0].value' -o tsv)

# Inline pull script. Workflow workflows/wan22-t2v.json references three exact filenames:
#   models/diffusion_models/wan2.2_ti2v_5B_fp16.safetensors
#   models/vae/wan_2.1_vae.safetensors
#   models/text_encoders/umt5_xxl_fp8_e4m3fn_scaled.safetensors
# We pull from Kijai/WanVideo_comfy (repackaged for ComfyUI core nodes) and rename
# to the canonical names so the workflow loads cleanly. If filenames in that repo
# drift, edit the globs below or rename manually on the share after the pull.
read -r -d '' PULL_SCRIPT <<'BASH' || true
set -euo pipefail
pip install --no-cache-dir "huggingface_hub[hf_transfer]" hf_transfer
export HF_HUB_ENABLE_HF_TRANSFER=1
mkdir -p /models/diffusion_models /models/vae /models/text_encoders /tmp/wan22

echo "==> downloading Kijai/WanVideo_comfy (Wan2.2 TI2V-5B + VAE + UMT5)"
huggingface-cli download Kijai/WanVideo_comfy \
  --include "*Wan2_2*TI2V*5B*fp16*.safetensors" \
            "*Wan2_1*VAE*.safetensors" \
            "*umt5_xxl*fp8_e4m3fn_scaled*.safetensors" \
  --local-dir /tmp/wan22 --local-dir-use-symlinks False

echo "==> renaming to canonical workflow filenames"
find /tmp/wan22 -name "*Wan2_2*TI2V*5B*.safetensors" -print -exec mv {} /models/diffusion_models/wan2.2_ti2v_5B_fp16.safetensors \;
find /tmp/wan22 -name "*Wan2_1*VAE*.safetensors"     -print -exec mv {} /models/vae/wan_2.1_vae.safetensors \;
find /tmp/wan22 -name "*umt5_xxl*.safetensors"       -print -exec mv {} /models/text_encoders/umt5_xxl_fp8_e4m3fn_scaled.safetensors \;

rm -rf /tmp/wan22
echo "==> done. Contents:"
ls -lhR /models
BASH

echo "==> launching ACI '$ACI_NAME' to pull weights into share '$FILESHARE'"
az container create \
  -g "$RG" -n "$ACI_NAME" \
  --image python:3.11-slim \
  --os-type Linux \
  --cpu 2 --memory 4 \
  --restart-policy Never \
  --azure-file-volume-account-name "$STORAGE_NAME" \
  --azure-file-volume-account-key "$STORAGE_KEY" \
  --azure-file-volume-share-name "$FILESHARE" \
  --azure-file-volume-mount-path "/models" \
  --command-line "/bin/bash -c \"$PULL_SCRIPT\"" \
  -o none

echo "==> streaming logs (this takes ~5-10 min)"
az container logs -g "$RG" -n "$ACI_NAME" --follow || true

echo "==> cleaning up ACI"
az container delete -g "$RG" -n "$ACI_NAME" --yes -o none

echo "==> done. Verify:"
az storage file list \
  --account-name "$STORAGE_NAME" --account-key "$STORAGE_KEY" \
  --share-name "$FILESHARE" --path diffusion_models -o table
