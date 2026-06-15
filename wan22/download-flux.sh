#!/usr/bin/env bash
# Add FLUX.1 Schnell (fp8 all-in-one checkpoint) to the EXISTING Wan2.2 model
# share so ComfyUI on the A100 can also generate character portraits — for ~$0
# extra GPU (it shares the A100 you already pay for when rendering video).
#
# FLUX.1 Schnell is Apache-2.0 and ungated on Hugging Face (no token needed).
# It loads with ComfyUI's core CheckpointLoaderSimple — no image rebuild required,
# the file just lands on the share that's already mounted at /opt/ComfyUI/models.
#
# Required env vars:
#   STORAGE_NAME  - storage account holding the 'models' file share (same one wan22 uses)
# Optional:
#   RG, LOC, FILESHARE
set -euo pipefail

RG="${RG:-rg-videotool}"
LOC="${LOC:-eastus}"
FILESHARE="${FILESHARE:-models}"
ACI_NAME="flux-weight-puller"

if [[ -z "${STORAGE_NAME:-}" ]]; then
  echo "ERROR: STORAGE_NAME is required (the storage account behind the 'models' share)." >&2
  echo "Find it with: az storage account list -g $RG --query \"[?contains(name,'wan22stor')].name\" -o tsv" >&2
  exit 1
fi

STORAGE_KEY=$(az storage account keys list -g "$RG" -n "$STORAGE_NAME" --query '[0].value' -o tsv)

# workflows/flux-schnell.json references exactly:
#   models/checkpoints/flux1-schnell-fp8.safetensors
read -r -d '' PULL_SCRIPT <<'BASH' || true
set -euo pipefail
# Use the Python API (hf_hub_download): the `huggingface-cli`/`hf` CLI surface keeps
# changing (huggingface-cli is now a no-op shim that prints "use hf instead"), and
# hf_xet is the current fast-transfer backend (hf_transfer is deprecated).
pip install --no-cache-dir "huggingface_hub>=0.34,<2" hf_xet
mkdir -p /models/checkpoints
DEST=/models/checkpoints/flux1-schnell-fp8.safetensors

if [[ -f "$DEST" ]] && [[ "$(stat -c%s "$DEST")" -gt 1000000000 ]]; then
  echo "==> $DEST already present ($(stat -c%s "$DEST") bytes); skipping download."
else
  echo "==> downloading Comfy-Org/flux1-schnell (fp8 all-in-one checkpoint, ~17 GB)"
  python -c "from huggingface_hub import hf_hub_download; print('saved to', hf_hub_download(repo_id='Comfy-Org/flux1-schnell', filename='flux1-schnell-fp8.safetensors', local_dir='/models/checkpoints'))"
fi

# Fail loudly if the file is missing or implausibly small, so the outer script's
# exit code (and our verify step) reflect reality instead of a silent no-op.
if [[ ! -f "$DEST" ]] || [[ "$(stat -c%s "$DEST")" -lt 1000000000 ]]; then
  echo "ERROR: download incomplete or missing at $DEST" >&2
  exit 1
fi

echo "==> done. checkpoints contents:"
ls -lh /models/checkpoints
BASH

# Base64-encode the script so the multi-line body (which itself contains double
# quotes and backslash line-continuations) survives ACI --command-line parsing
# intact. The container just decodes it and pipes it into bash.
PULL_B64=$(printf '%s' "$PULL_SCRIPT" | base64 | tr -d '\n')

echo "==> launching ACI '$ACI_NAME' to pull FLUX.1 Schnell into share '$FILESHARE'"
# Use a Microsoft Container Registry image to avoid Docker Hub anonymous-pull
# rate limits that intermittently fail ACI creates from Azure's shared egress IPs.
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

echo "==> streaming logs (this takes ~5-10 min for ~17 GB)"
az container logs -g "$RG" -n "$ACI_NAME" --follow || true

echo "==> cleaning up ACI"
az container delete -g "$RG" -n "$ACI_NAME" --yes -o none

echo "==> verify on the share:"
az storage file list \
  --account-name "$STORAGE_NAME" --account-key "$STORAGE_KEY" \
  --share-name "$FILESHARE" --path checkpoints -o table
