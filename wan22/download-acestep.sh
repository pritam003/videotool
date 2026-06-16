#!/usr/bin/env bash
# Stage the ACE-Step text-to-music checkpoint onto the EXISTING Wan2.2 model share so ComfyUI on
# the A100 can compose a real instrumental SCORE for each film — for ~$0 extra GPU (it shares the
# A100 you already pay for when rendering video). ACE-Step is Apache-2.0 (clean commercial use)
# and runs with ComfyUI's NATIVE ACE-Step nodes — no custom node needed, just the checkpoint here
# and a recent ComfyUI (the base image clones ComfyUI master, which has them).
#
# Mirrors wan22/download-flux.sh: a throwaway ACI mounts the share, pulls the weight, cleans up.
#
# Required env:
#   STORAGE_NAME  - storage account behind the 'models' share (same one wan22 uses, e.g. wan22stor22165)
# Optional:
#   RG, LOC, FILESHARE, ACE_REPO, ACE_FILE
set -euo pipefail

RG="${RG:-rg-videotool}"
LOC="${LOC:-eastus}"
FILESHARE="${FILESHARE:-models}"
# Comfy-Org repackages the checkpoint as a single all-in-one file for CheckpointLoaderSimple.
ACE_REPO="${ACE_REPO:-Comfy-Org/ACE-Step_ComfyUI_repackaged}"
ACE_FILE="${ACE_FILE:-all_in_one/ace_step_v1_3.5b.safetensors}"
ACI_NAME="acestep-weight-puller"

if [[ -z "${STORAGE_NAME:-}" ]]; then
  echo "ERROR: STORAGE_NAME is required (the storage account behind the 'models' share)." >&2
  echo "Find it with: az storage account list -g $RG --query \"[?contains(name,'wan22stor')].name\" -o tsv" >&2
  exit 1
fi

STORAGE_KEY=$(az storage account keys list -g "$RG" -n "$STORAGE_NAME" --query '[0].value' -o tsv)

# workflows/ace-step.json references exactly models/checkpoints/ace_step_v1_3.5b.safetensors
read -r -d '' PULL_SCRIPT <<BASH || true
set -euo pipefail
pip install --no-cache-dir "huggingface_hub>=0.34,<2" hf_xet
mkdir -p /models/checkpoints /tmp/ace
DEST=/models/checkpoints/ace_step_v1_3.5b.safetensors
if [[ -f "\$DEST" ]] && [[ "\$(stat -c%s "\$DEST")" -gt 1000000000 ]]; then
  echo "==> \$DEST already present; skipping download."
else
  echo "==> downloading ${ACE_REPO} :: ${ACE_FILE} (~3.5B checkpoint)"
  python -c "from huggingface_hub import hf_hub_download; print(hf_hub_download(repo_id='${ACE_REPO}', filename='${ACE_FILE}', local_dir='/tmp/ace'))"
  cp "/tmp/ace/${ACE_FILE}" "\$DEST"
fi
if [[ ! -f "\$DEST" ]] || [[ "\$(stat -c%s "\$DEST")" -lt 1000000000 ]]; then
  echo "ERROR: download incomplete or missing at \$DEST" >&2
  exit 1
fi
echo "==> done. checkpoints contents:"
ls -lh /models/checkpoints
BASH

PULL_B64=$(printf '%s' "$PULL_SCRIPT" | base64 | tr -d '\n')

echo "==> launching ACI '$ACI_NAME' to pull ACE-Step into share '$FILESHARE/checkpoints'"
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

echo "==> streaming logs (this takes ~5-10 min)"
az container logs -g "$RG" -n "$ACI_NAME" --follow || true

echo "==> cleaning up ACI"
az container delete -g "$RG" -n "$ACI_NAME" --yes -o none

echo "==> verify on the share:"
az storage file list \
  --account-name "$STORAGE_NAME" --account-key "$STORAGE_KEY" \
  --share-name "$FILESHARE" --path checkpoints -o table
