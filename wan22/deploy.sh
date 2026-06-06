#!/usr/bin/env bash
# One-shot deploy of ComfyUI + Wan2.2-TI2V-5B on Azure Container Apps (A100 Consumption GPU).
#
# Prereqs:
#   - Container App Environment 'videotool' already exists in rg-videotool / eastus.
#   - GPU quota approved (>=1 Consumption A100).
#
# Order:
#   1. Create ACR (image registry).
#   2. Create Storage Account + Azure Files share for model weights.
#   3. Run wan22/download-weights.sh ONCE to populate the share (~15 GB).
#   4. Build image with `az acr build` (cloud build — no local Docker needed).
#   5. Add A100 workload profile to the env.
#   6. Mount the Azure Files share to the env.
#   7. Create the container app (scale-to-zero).
set -euo pipefail

SUB="cc4e707a-06b6-43c5-85e1-3d6b406a33c2"
RG="rg-videotool"
LOC="eastus"
ENV_NAME="videotool"

ACR_NAME="${ACR_NAME:-wan22acr$RANDOM}"        # globally unique
STORAGE_NAME="${STORAGE_NAME:-wan22stor$RANDOM}" # globally unique, lowercase
FILESHARE="models"
OUTPUT_SHARE="outputs"
APP_NAME="wan22"
IMAGE_TAG="latest"
WORKLOAD_PROFILE="gpu-a100"
ENV_STORAGE_NAME="wan22models"
ENV_OUTPUT_STORAGE_NAME="wan22outputs"

az account set --subscription "$SUB"

echo "==> ACR $ACR_NAME"
az acr create -g "$RG" -n "$ACR_NAME" --sku Basic --admin-enabled true -o none

echo "==> Storage $STORAGE_NAME + file shares '$FILESHARE' (models) and '$OUTPUT_SHARE' (outputs)"
az storage account create -g "$RG" -n "$STORAGE_NAME" -l "$LOC" \
  --sku Standard_LRS --kind StorageV2 -o none
STORAGE_KEY=$(az storage account keys list -g "$RG" -n "$STORAGE_NAME" --query '[0].value' -o tsv)
az storage share-rm create \
  --resource-group "$RG" --storage-account "$STORAGE_NAME" \
  --name "$FILESHARE" --quota 100 -o none
az storage share-rm create \
  --resource-group "$RG" --storage-account "$STORAGE_NAME" \
  --name "$OUTPUT_SHARE" --quota 100 -o none

echo
echo "==================================================================="
echo "PAUSE: populate the file share with Wan2.2 weights now."
echo "Run, in another shell:"
echo "  STORAGE_NAME=$STORAGE_NAME bash wan22/download-weights.sh"
echo "Press Enter when done to continue with image build + deploy."
echo "==================================================================="
read -r _

echo "==> building image via ACR Tasks (no local Docker required)"
az acr build \
  --registry "$ACR_NAME" \
  --image "wan22-comfyui:${IMAGE_TAG}" \
  --platform linux/amd64 \
  --file wan22/Dockerfile \
  wan22/

echo "==> adding A100 Consumption workload profile to env"
az containerapp env workload-profile add \
  -g "$RG" -n "$ENV_NAME" \
  --workload-profile-name "$WORKLOAD_PROFILE" \
  --workload-profile-type "Consumption-GPU-NC24-A100" \
  -o none || echo "  (workload profile may already exist)"

echo "==> linking Azure Files shares to env (models + outputs)"
az containerapp env storage set \
  -g "$RG" -n "$ENV_NAME" \
  --storage-name "$ENV_STORAGE_NAME" \
  --azure-file-account-name "$STORAGE_NAME" \
  --azure-file-account-key "$STORAGE_KEY" \
  --azure-file-share-name "$FILESHARE" \
  --access-mode ReadWrite \
  -o none
az containerapp env storage set \
  -g "$RG" -n "$ENV_NAME" \
  --storage-name "$ENV_OUTPUT_STORAGE_NAME" \
  --azure-file-account-name "$STORAGE_NAME" \
  --azure-file-account-key "$STORAGE_KEY" \
  --azure-file-share-name "$OUTPUT_SHARE" \
  --access-mode ReadWrite \
  -o none

echo "==> creating container app $APP_NAME"
ACR_PASS=$(az acr credential show -n "$ACR_NAME" --query passwords[0].value -o tsv)
az containerapp create \
  -g "$RG" -n "$APP_NAME" \
  --environment "$ENV_NAME" \
  --workload-profile-name "$WORKLOAD_PROFILE" \
  --image "$ACR_NAME.azurecr.io/wan22-comfyui:${IMAGE_TAG}" \
  --registry-server "$ACR_NAME.azurecr.io" \
  --registry-username "$ACR_NAME" \
  --registry-password "$ACR_PASS" \
  --target-port 8188 \
  --ingress external \
  --transport auto \
  --cpu 24 --memory 220Gi \
  --min-replicas 0 --max-replicas 1 \
  --env-vars "COMFYUI_PORT=8188" \
  -o none

echo "==> mounting file shares into the container (models, output)"
# The CLI does not expose volume mounts at create time for managed env storage;
# patch the YAML to add the volumes + volumeMounts.
TMP_YAML=$(mktemp)
az containerapp show -g "$RG" -n "$APP_NAME" -o yaml > "$TMP_YAML"
python3 - "$TMP_YAML" "$ENV_STORAGE_NAME" "$ENV_OUTPUT_STORAGE_NAME" <<'PY'
import sys, yaml
path, models_storage, output_storage = sys.argv[1], sys.argv[2], sys.argv[3]
with open(path) as f:
    doc = yaml.safe_load(f)
tpl = doc["properties"]["template"]
tpl.setdefault("volumes", []).extend([
    {"name": "models", "storageType": "AzureFile", "storageName": models_storage},
    {"name": "output", "storageType": "AzureFile", "storageName": output_storage},
])
container = tpl["containers"][0]
container.setdefault("volumeMounts", []).extend([
    {"volumeName": "models", "mountPath": "/opt/ComfyUI/models"},
    {"volumeName": "output", "mountPath": "/opt/ComfyUI/output"},
])
with open(path, "w") as f:
    yaml.safe_dump(doc, f)
PY

az containerapp update -g "$RG" -n "$APP_NAME" --yaml "$TMP_YAML" -o none
rm -f "$TMP_YAML"

FQDN=$(az containerapp show -g "$RG" -n "$APP_NAME" --query properties.configuration.ingress.fqdn -o tsv)

echo
echo "==================================================================="
echo "DONE."
echo "  ComfyUI URL:  https://$FQDN"
echo "  ACR:          $ACR_NAME.azurecr.io"
echo "  Storage:      $STORAGE_NAME (share: $FILESHARE)"
echo "==================================================================="
echo "First request will take ~3-5 min (cold start + Wan2.2 weight load)."
echo "Idle cost: \$0  |  Running cost: ~\$3.40/hr while serving."
