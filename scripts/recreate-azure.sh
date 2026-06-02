#!/usr/bin/env bash
# Recreate the videotool Azure stack from scratch.
# Idempotent where possible; reuses existing Entra app reg + security group.
# Usage: bash scripts/recreate-azure.sh
set -euo pipefail

# ---- config (edit if needed) ----
SUB="cc4e707a-06b6-43c5-85e1-3d6b406a33c2"
TENANT="2c2d753c-7844-4a95-8ed4-3844729e0803"
RG="rg-videotool"
APP_LOCATION="centralus"
AOAI_LOCATION="eastus2"
ASP_NAME="asp-videotool"
WEBAPP_NAME="videotool-pritam003-23209"     # must be globally unique
AOAI_NAME="videotool-aoai"
STORAGE_NAME="videotoolstor$(printf '%04d' $((RANDOM % 10000)))"
STORAGE_CONTAINER="videos"
APP_ID="054ed937-90d8-4874-94db-9cb7db9214bc"
GROUP_ID="913b2179-36bb-4451-97cd-e420657529b7"
GH_REPO="https://github.com/pritam003/videotool.git"
GH_BRANCH="main"

echo "==> selecting subscription"
az account set --subscription "$SUB"

echo "==> creating resource group $RG ($APP_LOCATION)"
az group create -n "$RG" -l "$APP_LOCATION" -o none

echo "==> creating storage account $STORAGE_NAME ($AOAI_LOCATION)"
az storage account create \
  -g "$RG" -n "$STORAGE_NAME" \
  -l "$AOAI_LOCATION" \
  --sku Standard_LRS --kind StorageV2 \
  --allow-blob-public-access false \
  -o none

STORAGE_KEY=$(az storage account keys list -g "$RG" -n "$STORAGE_NAME" --query '[0].value' -o tsv)

echo "==> creating blob container $STORAGE_CONTAINER"
az storage container create \
  --account-name "$STORAGE_NAME" --account-key "$STORAGE_KEY" \
  -n "$STORAGE_CONTAINER" -o none

echo "==> creating Azure AI Services (AOAI) $AOAI_NAME ($AOAI_LOCATION)"
az cognitiveservices account create \
  -g "$RG" -n "$AOAI_NAME" \
  -l "$AOAI_LOCATION" \
  --kind AIServices --sku S0 \
  --custom-domain "$AOAI_NAME" \
  --yes -o none

echo "==> creating sora-2 deployment (capacity 1, GlobalStandard)"
az cognitiveservices account deployment create \
  -g "$RG" -n "$AOAI_NAME" \
  --deployment-name sora-2 \
  --model-name sora-2 --model-version 2025-12-08 --model-format OpenAI \
  --sku-name GlobalStandard --sku-capacity 1 \
  -o none || echo "  (sora-2 deploy may need manual capacity request)"

echo "==> creating gpt-5-mini deployment (capacity 50, GlobalStandard)"
az cognitiveservices account deployment create \
  -g "$RG" -n "$AOAI_NAME" \
  --deployment-name gpt-5-mini \
  --model-name gpt-5-mini --model-version 2025-08-07 --model-format OpenAI \
  --sku-name GlobalStandard --sku-capacity 50 \
  -o none

AOAI_ENDPOINT=$(az cognitiveservices account show -g "$RG" -n "$AOAI_NAME" --query properties.endpoint -o tsv)
AOAI_KEY=$(az cognitiveservices account keys list -g "$RG" -n "$AOAI_NAME" --query key1 -o tsv)

echo "==> creating App Service plan $ASP_NAME (Linux F1 Free)"
az appservice plan create \
  -g "$RG" -n "$ASP_NAME" \
  -l "$APP_LOCATION" \
  --is-linux --sku F1 \
  -o none

echo "==> creating webapp $WEBAPP_NAME (.NET 8)"
az webapp create \
  -g "$RG" -n "$WEBAPP_NAME" \
  --plan "$ASP_NAME" \
  --runtime "DOTNETCORE:8.0" \
  -o none

echo "==> minting fresh client secret on app reg $APP_ID"
SECRET_JSON=$(az ad app credential reset --id "$APP_ID" --display-name "videotool-$(date +%Y%m%d)" --years 2 -o json)
CLIENT_SECRET=$(echo "$SECRET_JSON" | jq -r .password)

echo "==> writing app settings"
az webapp config appsettings set \
  -g "$RG" -n "$WEBAPP_NAME" \
  --settings \
    AOAI_ENDPOINT="$AOAI_ENDPOINT" \
    AOAI_KEY="$AOAI_KEY" \
    AOAI_DEPLOYMENT="sora-2" \
    CHAT_DEPLOYMENT="gpt-5-mini" \
    REASONING_DEPLOYMENT="gpt-5-mini" \
    SPEECH_REGION="$AOAI_LOCATION" \
    STORAGE_ACCOUNT="$STORAGE_NAME" \
    STORAGE_CONTAINER="$STORAGE_CONTAINER" \
    AUTH_REQUIRED="1" \
    ALLOWED_GROUP_ID="$GROUP_ID" \
    MICROSOFT_PROVIDER_AUTHENTICATION_SECRET="$CLIENT_SECRET" \
  -o none

echo "==> granting webapp managed identity Storage Blob Data Contributor on storage"
az webapp identity assign -g "$RG" -n "$WEBAPP_NAME" -o none
WEBAPP_PRINCIPAL=$(az webapp identity show -g "$RG" -n "$WEBAPP_NAME" --query principalId -o tsv)
STORAGE_ID=$(az storage account show -g "$RG" -n "$STORAGE_NAME" --query id -o tsv)
az role assignment create \
  --assignee "$WEBAPP_PRINCIPAL" \
  --role "Storage Blob Data Contributor" \
  --scope "$STORAGE_ID" \
  -o none || echo "  (role assignment may already exist)"

echo "==> configuring Easy Auth v2"
WEBAPP_HOST=$(az webapp show -g "$RG" -n "$WEBAPP_NAME" --query defaultHostName -o tsv)
AUTH_BODY=$(cat <<JSON
{
  "properties": {
    "platform": { "enabled": true, "runtimeVersion": "~1" },
    "globalValidation": {
      "requireAuthentication": true,
      "unauthenticatedClientAction": "RedirectToLoginPage",
      "redirectToProvider": "azureactivedirectory",
      "excludedPaths": ["/health", "/.auth/*", "/sw.js", "/favicon.ico", "/api/push/vapid-public-key"]
    },
    "identityProviders": {
      "azureActiveDirectory": {
        "enabled": true,
        "registration": {
          "openIdIssuer": "https://login.microsoftonline.com/$TENANT/v2.0",
          "clientId": "$APP_ID",
          "clientSecretSettingName": "MICROSOFT_PROVIDER_AUTHENTICATION_SECRET"
        },
        "validation": {
          "allowedAudiences": ["api://$APP_ID", "$APP_ID"]
        }
      }
    },
    "login": {
      "tokenStore": { "enabled": true }
    }
  }
}
JSON
)
az rest --method PUT \
  --url "https://management.azure.com/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.Web/sites/$WEBAPP_NAME/config/authsettingsV2?api-version=2022-03-01" \
  --body "$AUTH_BODY" \
  -o none

echo "==> verifying app reg redirect URI matches"
EXPECTED_REDIRECT="https://$WEBAPP_HOST/.auth/login/aad/callback"
az ad app update --id "$APP_ID" --web-redirect-uris "$EXPECTED_REDIRECT" -o none

echo "==> wiring GitHub deployment ($GH_REPO branch $GH_BRANCH)"
az webapp deployment source config \
  -g "$RG" -n "$WEBAPP_NAME" \
  --repo-url "$GH_REPO" --branch "$GH_BRANCH" --manual-integration \
  -o none

echo
echo "==================================================================="
echo "DONE."
echo "  Webapp:    https://$WEBAPP_HOST"
echo "  Storage:   $STORAGE_NAME"
echo "  AOAI:      $AOAI_ENDPOINT"
echo "  Group ID:  $GROUP_ID  (add users with: az ad group member add ...)"
echo "==================================================================="
echo "Verify health:  curl -sf https://$WEBAPP_HOST/health"
echo "Trigger initial deploy:  az webapp deployment source sync -g $RG -n $WEBAPP_NAME"
