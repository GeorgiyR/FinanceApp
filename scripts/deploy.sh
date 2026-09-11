#!/bin/bash
set -euo pipefail

APP_DIR="/var/FinanceApp"
PUBLISH_DIR="$APP_DIR/publish"
SERVICE="financeapp-api.service"
TEMP_DIR="/tmp/FinanceApp-publish-$$"
BACKUP_DIR="$APP_DIR/publish-backup-$(date +%Y%m%d-%H%M%S)"

cleanup() {
    rm -rf "$TEMP_DIR"
}

rollback() {
    echo
    echo "==> DEPLOY FAILED - rolling back..."

    systemctl stop "$SERVICE" || true

    if [[ -d "$BACKUP_DIR" ]]; then
        rm -rf "$PUBLISH_DIR"
        mv "$BACKUP_DIR" "$PUBLISH_DIR"
        chown -R www-data:www-data "$PUBLISH_DIR"
        systemctl start "$SERVICE" || true
    fi

    cleanup

    echo "==> Rollback completed."
    systemctl --no-pager --full status "$SERVICE" || true
}

trap cleanup EXIT
trap rollback ERR

echo "========================================"
echo " FinanceApp deployment"
echo "========================================"

cd "$APP_DIR"

echo "==> Checking Git status..."
if [[ -n "$(git status --porcelain)" ]]; then
    echo "ERROR: Git working tree is not clean."
    git status --short
    exit 1
fi

echo "==> Fetching origin..."
git fetch origin

echo "==> Updating source..."
git pull --ff-only origin main

echo "==> Building application..."
rm -rf "$TEMP_DIR"
mkdir -p "$TEMP_DIR"

dotnet publish FinanceApp.API/FinanceApp.API.csproj \
    -c Release \
    -o "$TEMP_DIR" \
    --no-self-contained

echo "==> Verifying published application..."
test -f "$TEMP_DIR/FinanceApp.API.dll"

echo "==> Saving current publish..."
mv "$PUBLISH_DIR" "$BACKUP_DIR"

echo "==> Installing new publish..."
mv "$TEMP_DIR" "$PUBLISH_DIR"

echo "==> Restoring production configuration..."
cp "$APP_DIR/FinanceApp.API/appsettings.json" \
   "$PUBLISH_DIR/appsettings.json"

echo "==> Setting permissions..."
chown -R www-data:www-data "$PUBLISH_DIR"

echo "==> Restarting service..."
systemctl restart "$SERVICE"

echo "==> Waiting for application..."
sleep 3

echo "==> Checking service..."
if ! systemctl is-active --quiet "$SERVICE"; then
    echo "ERROR: Service is not running."
    exit 1
fi

echo
echo "========================================"
echo " DEPLOYMENT SUCCESSFUL"
echo "========================================"
echo
echo "Commit:"
git log -1 --oneline
echo
echo "Service:"
systemctl --no-pager --full status "$SERVICE"
echo
echo "Backup:"
echo "$BACKUP_DIR"
