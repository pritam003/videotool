#!/usr/bin/env bash
# Stage the HunyuanVideo-Foley weights onto the EXISTING Wan2.2 model share so the
# audio-enabled ComfyUI image (wan22/Dockerfile.audio) can synthesize synced SFX/ambience
# on the same A100 you already pay for when rendering video — for ~$0 extra.
#
# This mirrors wan22/download-flux.sh: it launches a throwaway ACI with the model share
# mounted, pulls the weights with the Hugging Face python API, verifies, and cleans up.
#
# Required env:
#   STORAGE_NAME  - storage account behind the 'models' share (same one wan22 uses, e.g. wan22stor22165)
# Optional:
#   RG, LOC, FILESHARE, FOLEY_REPO, FOLEY_DEST
#
# NOTE: confirm FOLEY_DEST matches where YOUR installed ComfyUI-HunyuanVideoFoley node looks
# for its weights (some nodes expect models/foley, others models/hunyuanvideo-foley). Adjust
# FOLEY_DEST (and the node config) so they agree, then re-run.
set -euo pipefail

RG="${RG:-rg-videotool}"
LOC="${LOC:-eastus}"
FILESHARE="${FILESHARE:-models}"
FOLEY_REPO="${FOLEY_REPO:-tencent/HunyuanVideo-Foley}"
FOLEY_DEST="${FOLEY_DEST:-hunyuanvideo-foley}"   # under <share>/<FOLEY_DEST>
ACI_NAME="foley-weight-puller"

if [[ -z "${STORAGE_NAME:-}" ]]; then
  echo "ERROR: STORAGE_NAME is required (the storage account behind the 'models' share)." >&2
  echo "Find it with: az storage account list -g $RG --query \"[?contains(name,'wan22stor')].name\" -o tsv" >&2
  exit 1
fi

STORAGE_KEY=$(az storage account keys list -g "$RG" -n "$STORAGE_NAME" --query '[0].value' -o tsv)

read -r -d '' PULL_SCRIPT <<BASH || true
set -euo pipefail
pip install --no-cache-dir "huggingface_hub>=0.34,<2" hf_xet
DEST=/models/${FOLEY_DEST}
mkdir -p "\$DEST"
echo "==> downloading ${FOLEY_REPO} into \$DEST (several GB; this takes a while)"
python -c "from huggingface_hub import snapshot_download; print('saved to', snapshot_download(repo_id='${FOLEY_REPO}', local_dir='\$DEST'))"
echo "==> done. contents:"
ls -lhR "\$DEST" | head -50
BASH

PULL_B64=$(printf '%s' "$PULL_SCRIPT" | base64 | tr -d '\n')

echo "==> launching ACI '$ACI_NAME' to pull ${FOLEY_REPO} into share '$FILESHARE/$FOLEY_DEST'"
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

echo "==> streaming logs"
az container logs -g "$RG" -n "$ACI_NAME" --follow || true

echo "==> cleaning up ACI"
az container delete -g "$RG" -n "$ACI_NAME" --yes -o none

echo "==> verify on the share:"
az storage file list \
  --account-name "$STORAGE_NAME" --account-key "$STORAGE_KEY" \
  --share-name "$FILESHARE" --path "$FOLEY_DEST" -o table
