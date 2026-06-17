#!/usr/bin/env bash
# Recreate ONLY the wan22 GPU Container App (the A100 ComfyUI backend) after it
# was deleted to park costs. Fast: reuses the EXISTING ACR image and the EXISTING
# model file share — NO image rebuild, NO 121 GB model re-download.
#
# Prereqs that MUST still exist (do NOT delete these to keep recreation fast):
#   - ACR  wan22acr23247  -> holds image wan22-comfyui:latest (~5.5 GB)
#   - Storage wan22stor22165 / share 'models' -> ~121 GB of weights
#   - Container App Env 'videotool' with workload profile 'gpu-a100'
#     and storage handles wan22models + wan22outputs2 (these persist with the env)
#
# Usage: bash scripts/recreate-wan22-gpu.sh
set -euo pipefail

RG="rg-videotool"
ENV="videotool"
APP="wan22"
ACR="wan22acr23247"
IMAGE="${ACR}.azurecr.io/wan22-comfyui:latest"
PROFILE="gpu-a100"

echo "==> ensuring ACR admin is enabled (for image pull)"
az acr update -n "$ACR" --admin-enabled true -o none
ACR_USER=$(az acr credential show -n "$ACR" --query username -o tsv)
ACR_PASS=$(az acr credential show -n "$ACR" --query "passwords[0].value" -o tsv)

echo "==> creating container app $APP (image pulls from ACR, ~20s — no rebuild)"
az containerapp create -g "$RG" -n "$APP" \
  --environment "$ENV" \
  --image "$IMAGE" \
  --workload-profile-name "$PROFILE" \
  --cpu 24 --memory 220Gi \
  --ingress external --target-port 8188 \
  --min-replicas 0 --max-replicas 1 \
  --registry-server "${ACR}.azurecr.io" \
  --registry-username "$ACR_USER" \
  --registry-password "$ACR_PASS" \
  --env-vars \
    COMFYUI_PORT=8188 \
    "PYTORCH_CUDA_ALLOC_CONF=expandable_segments:True,max_split_size_mb:512" \
    LLM_GPU_LAYERS=0 \
    LLM_CTX=4096 \
    COMFY_RESERVE_VRAM=7 \
  -o none

echo "==> attaching the EXISTING model + output shares (no re-download)"
# CLI create can't take volumes, so patch the template with a YAML merge.
TMP_YAML=$(mktemp)
cat > "$TMP_YAML" <<YAML
properties:
  template:
    containers:
    - name: $APP
      image: $IMAGE
      volumeMounts:
      - volumeName: models
        mountPath: /opt/ComfyUI/models
      - volumeName: output
        mountPath: /opt/ComfyUI/output
    volumes:
    - name: models
      storageName: wan22models
      storageType: AzureFile
    - name: output
      storageName: wan22outputs2
      storageType: AzureFile
YAML
az containerapp update -g "$RG" -n "$APP" --yaml "$TMP_YAML" -o none
rm -f "$TMP_YAML"

FQDN=$(az containerapp show -g "$RG" -n "$APP" --query "properties.configuration.ingress.fqdn" -o tsv)
echo "==> done. wan22 FQDN: https://$FQDN"
echo "    (scale-to-zero: idles to \$0 between renders; cold start ~45s + model load)"
echo "    If the FQDN differs from the old one, update the App Service setting:"
echo "      az webapp config appsettings set -g $RG -n videotool-pritam003-23209 \\"
echo "        --settings WAN_BASE_URL=https://$FQDN"
