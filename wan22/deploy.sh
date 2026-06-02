#!/usr/bin/env bash
# One-shot deploy of Wan 2.2 TI2V-5B to Azure Container Apps Serverless GPU.
# PREREQ: A100 GPU quota approved on the subscription.
#         Run: az quota show --resource-name "standardNCADSA100v4Family" \
#              --scope "/subscriptions/<SUB_ID>/providers/Microsoft.Compute/locations/<REGION>"
#
# Usage: bash deploy.sh
set -euo pipefail

# ---- edit these ----
SUB_ID="cc4e707a-06b6-43c5-85e1-3d6b406a33c2"
RG="rg-videotool-wan22"
LOCATION="swedencentral"          # GPU SKU availability — verify with `az containerapp env workload-profile list-supported`
ACR_NAME="videotoolacrwan22"
ENV_NAME="wan22-env"
APP_NAME="wan22-gpu"
BLOB_ACCOUNT="videotoolstor6085"  # reuse existing storage
BLOB_CONTAINER="videos"
GPU_PROFILE="Consumption-GPU-NC24-A100"
# --------------------

echo "==> Setting subscription"
az account set --subscription "$SUB_ID"

echo "==> Creating resource group"
az group create -n "$RG" -l "$LOCATION" -o none

echo "==> Creating ACR"
az acr create -n "$ACR_NAME" -g "$RG" --sku Standard --admin-enabled true -o none

echo "==> Building image (this may take 20-40 min — bakes 28GB weights)"
az acr build --registry "$ACR_NAME" --image "wan22:v1" --file Dockerfile .

echo "==> Creating Container Apps environment with GPU workload profile"
az containerapp env create \
    -n "$ENV_NAME" -g "$RG" -l "$LOCATION" \
    --enable-workload-profiles -o none

az containerapp env workload-profile add \
    -n "$ENV_NAME" -g "$RG" \
    --workload-profile-name "$GPU_PROFILE" \
    --workload-profile-type "$GPU_PROFILE" \
    --min-nodes 0 --max-nodes 1 -o none

echo "==> Generating shared bearer token"
WAN_TOKEN="$(openssl rand -hex 32)"
echo "    WAN_AUTH_TOKEN = $WAN_TOKEN"
echo "    (save this — App Service must use it)"

echo "==> Looking up storage key"
BLOB_KEY="$(az storage account keys list -n "$BLOB_ACCOUNT" --query '[0].value' -o tsv)"

echo "==> Deploying container app"
ACR_PASS="$(az acr credential show -n "$ACR_NAME" --query 'passwords[0].value' -o tsv)"
ACR_USER="$(az acr credential show -n "$ACR_NAME" --query 'username' -o tsv)"

az containerapp create \
    -n "$APP_NAME" -g "$RG" \
    --environment "$ENV_NAME" \
    --workload-profile-name "$GPU_PROFILE" \
    --image "$ACR_NAME.azurecr.io/wan22:v1" \
    --registry-server "$ACR_NAME.azurecr.io" \
    --registry-username "$ACR_USER" \
    --registry-password "$ACR_PASS" \
    --target-port 8080 \
    --ingress external \
    --min-replicas 0 --max-replicas 1 \
    --scale-rule-name http --scale-rule-http-concurrency 1 \
    --secrets "wan-token=$WAN_TOKEN" "blob-key=$BLOB_KEY" \
    --env-vars \
        "WAN_AUTH_TOKEN=secretref:wan-token" \
        "BLOB_ACCOUNT=$BLOB_ACCOUNT" \
        "BLOB_CONTAINER=$BLOB_CONTAINER" \
        "BLOB_ACCOUNT_KEY=secretref:blob-key" \
    -o none

FQDN="$(az containerapp show -n "$APP_NAME" -g "$RG" --query 'properties.configuration.ingress.fqdn' -o tsv)"
echo ""
echo "==> Done."
echo "    Endpoint:  https://$FQDN"
echo "    Test:      curl https://$FQDN/health"
echo ""
echo "==> Configure App Service:"
echo "    az webapp config appsettings set -g <rg> -n videotool-pritam003-23209 \\"
echo "      --settings LOCAL_VIDEO_ENDPOINT=https://$FQDN \\"
echo "                 LOCAL_VIDEO_TOKEN=$WAN_TOKEN"
