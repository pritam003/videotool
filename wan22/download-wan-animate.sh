#!/usr/bin/env bash
# Stage the Wan2.2-Animate-14B weights onto the EXISTING Wan2.2 model share so ComfyUI on the A100
# can drive a character image with a sample/driving video (character animation). Wan-Animate is
# Apache-2.0 and built on Wan2.2-I2V, so it reuses the umt5 text encoder + wan_2.1 VAE you already
# stage for I2V — this script only pulls the Animate diffusion model + the (replacement-mode)
# Relighting LoRA.
#
# Mirrors wan22/download-acestep.sh: a throwaway ACI mounts the share, pulls the weights, cleans up.
#
# AFTER staging, activate the feature:
#   1) Make sure the GPU ComfyUI image has the `WanAnimateToVideo` node (ComfyUI core, recent) and
#      VideoHelperSuite (VHS_LoadVideo / VHS_VideoCombine). If not, rebuild the image with them.
#      The official ComfyUI Wan-Animate template also inserts DWPose pose/face PREPROCESSING between
#      the loaded video and WanAnimateToVideo — validate workflows/wan-animate.json against the
#      installed node (ComfyUI's 400 validation errors name the exact missing/extra input).
#   2) Set app setting WAN_ANIMATE_ENABLED=1 on the App Service, then `az webapp restart`
#      (WanClient caches the workflow JSON via Lazy<string>).
#
# Required env:
#   STORAGE_NAME  - storage account behind the 'models' share (same one wan22 uses, e.g. wan22stor22165)
# Optional:
#   RG, LOC, FILESHARE, ANIMATE_REPO, ANIMATE_FILE, RELIGHT_FILE
set -euo pipefail

RG="${RG:-rg-videotool}"
LOC="${LOC:-eastus}"
FILESHARE="${FILESHARE:-models}"
# Comfy-Org repackages the ComfyUI-ready single-file weights under split_files/.
ANIMATE_REPO="${ANIMATE_REPO:-Comfy-Org/Wan_2.2_ComfyUI_Repackaged}"
ANIMATE_FILE="${ANIMATE_FILE:-split_files/diffusion_models/wan2.2_animate_14B_bf16.safetensors}"
RELIGHT_FILE="${RELIGHT_FILE:-split_files/loras/wan2.2_animate_14B_relight_lora_bf16.safetensors}"
ACI_NAME="wananimate-weight-puller"

if [[ -z "${STORAGE_NAME:-}" ]]; then
  echo "ERROR: STORAGE_NAME is required (the storage account behind the 'models' share)." >&2
  echo "Find it with: az storage account list -g $RG --query \"[?contains(name,'wan22stor')].name\" -o tsv" >&2
  exit 1
fi

STORAGE_KEY=$(az storage account keys list -g "$RG" -n "$STORAGE_NAME" --query '[0].value' -o tsv)

# workflows/wan-animate.json references models/diffusion_models/wan2.2_animate_14B_bf16.safetensors
# and (for replacement mode) models/loras/wan2.2_animate_14B_relight_lora_bf16.safetensors
read -r -d '' PULL_SCRIPT <<BASH || true
set -euo pipefail
pip install --no-cache-dir "huggingface_hub>=0.34,<2" hf_xet
mkdir -p /models/diffusion_models /models/loras /tmp/an
DEST=/models/diffusion_models/wan2.2_animate_14B_bf16.safetensors
if [[ -f "\$DEST" ]] && [[ "\$(stat -c%s "\$DEST")" -gt 20000000000 ]]; then
  echo "==> \$DEST already present; skipping diffusion model download."
else
  echo "==> downloading ${ANIMATE_REPO} :: ${ANIMATE_FILE} (~34.5 GB)"
  python -c "from huggingface_hub import hf_hub_download; print(hf_hub_download(repo_id='${ANIMATE_REPO}', filename='${ANIMATE_FILE}', local_dir='/tmp/an'))"
  cp "/tmp/an/${ANIMATE_FILE}" "\$DEST"
fi
RDEST=/models/loras/wan2.2_animate_14B_relight_lora_bf16.safetensors
if [[ -f "\$RDEST" ]] && [[ "\$(stat -c%s "\$RDEST")" -gt 500000000 ]]; then
  echo "==> \$RDEST already present; skipping relight LoRA download."
else
  echo "==> downloading ${ANIMATE_REPO} :: ${RELIGHT_FILE} (~1.4 GB, replacement mode)"
  python -c "from huggingface_hub import hf_hub_download; print(hf_hub_download(repo_id='${ANIMATE_REPO}', filename='${RELIGHT_FILE}', local_dir='/tmp/an'))"
  cp "/tmp/an/${RELIGHT_FILE}" "\$RDEST"
fi
if [[ ! -f "\$DEST" ]] || [[ "\$(stat -c%s "\$DEST")" -lt 20000000000 ]]; then
  echo "ERROR: animate model download incomplete or missing at \$DEST" >&2
  exit 1
fi
echo "==> done."
ls -lh /models/diffusion_models /models/loras
BASH

PULL_B64=$(printf '%s' "$PULL_SCRIPT" | base64 | tr -d '\n')

echo "==> launching ACI '$ACI_NAME' to pull Wan-Animate into share '$FILESHARE'"
az container create \
  -g "$RG" -n "$ACI_NAME" \
  --image mcr.microsoft.com/devcontainers/python:3.11 \
  --os-type Linux \
  --cpu 2 --memory 8 \
  --restart-policy Never \
  --azure-file-volume-account-name "$STORAGE_NAME" \
  --azure-file-volume-account-key "$STORAGE_KEY" \
  --azure-file-volume-share-name "$FILESHARE" \
  --azure-file-volume-mount-path "/models" \
  --command-line "/bin/bash -c \"echo $PULL_B64 | base64 -d | bash\"" \
  -o none

echo "==> streaming logs (this takes ~15-30 min — the model is ~34.5 GB)"
az container logs -g "$RG" -n "$ACI_NAME" --follow || true

echo "==> cleaning up ACI"
az container delete -g "$RG" -n "$ACI_NAME" --yes -o none

echo "==> verify on the share:"
az storage file list \
  --account-name "$STORAGE_NAME" --account-key "$STORAGE_KEY" \
  --share-name "$FILESHARE" --path diffusion_models -o table
