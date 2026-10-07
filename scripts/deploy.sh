#!/bin/bash
set -euo pipefail

APP_DIR="/var/FinanceApp"

API_PROJECT="$APP_DIR/FinanceApp.API"
FRONTEND_DIR="$APP_DIR/FinanceApp.Frontend"

PUBLISH_DIR="$APP_DIR/publish"
FRONTEND_DIST="$FRONTEND_DIR/dist"

SERVICE="financeapp-api.service"

TEMP_API_DIR="/tmp/FinanceApp-api-$$"
TEMP_FRONTEND_DIR="/tmp/FinanceApp-frontend-$$"

TIMESTAMP="$(date +%Y%m%d-%H%M%S)"

BACKUP_API_DIR="$APP_DIR/publish-backup-$TIMESTAMP"
BACKUP_FRONTEND_DIR="$APP_DIR/frontend-dist-backup-$TIMESTAMP"

DEPLOY_STARTED=0
DEPLOY_FINISHED=0
API_SWAPPED=0
FRONTEND_SWAPPED=0

cleanup() {
    rm -rf "$TEMP_API_DIR"
    rm -rf "$TEMP_FRONTEND_DIR"
}

rollback() {
    local exit_code=$?

    if [[ "$DEPLOY_FINISHED" -eq 1 ]]; then
        return "$exit_code"
    fi

    echo
    echo "========================================"
    echo " DEPLOYMENT FAILED"
    echo " Starting rollback..."
    echo "========================================"

    set +e

    #
    # Restore frontend
    #
    if [[ "$FRONTEND_SWAPPED" -eq 1 ]]; then
        echo "==> Rolling back frontend..."

        rm -rf "$FRONTEND_DIST"

        if [[ -d "$BACKUP_FRONTEND_DIR" ]]; then
            mv "$BACKUP_FRONTEND_DIR" "$FRONTEND_DIST"
            echo "    Frontend restored."
        else
            echo "    WARNING: Frontend backup not found."
        fi
    fi

    #
    # Restore backend
    #
    if [[ "$API_SWAPPED" -eq 1 ]]; then
        echo "==> Rolling back backend..."

        systemctl stop "$SERVICE" || true

        rm -rf "$PUBLISH_DIR"

        if [[ -d "$BACKUP_API_DIR" ]]; then
            mv "$BACKUP_API_DIR" "$PUBLISH_DIR"
            chown -R www-data:www-data "$PUBLISH_DIR"
            echo "    Backend restored."
        else
            echo "    WARNING: Backend backup not found."
        fi
    fi

    #
    # Start backend again
    #
    echo "==> Starting backend service..."
    systemctl start "$SERVICE" || true

    #
    # Cleanup temporary files
    #
    cleanup

    echo
    echo "========================================"
    echo " ROLLBACK COMPLETED"
    echo "========================================"

    systemctl --no-pager --full status "$SERVICE" || true

    return "$exit_code"
}

trap rollback ERR
trap cleanup EXIT

echo
echo "========================================"
echo " FinanceApp deployment"
echo "========================================"
echo

DEPLOY_STARTED=1

#
# 1. Check Git
#
echo "==> Checking Git status..."

cd "$APP_DIR"

if [[ -n "$(git status --porcelain)" ]]; then
    echo
    echo "ERROR: Git working tree is not clean."
    echo
    git status --short
    exit 1
fi

#
# 2. Update source code
#
echo
echo "==> Fetching origin..."
git fetch origin

echo
echo "==> Updating source..."
git pull --ff-only origin main

echo
echo "==> Current commit:"
git log -1 --oneline

#
# 3. Build frontend
#
echo
echo "========================================"
echo " Building frontend"
echo "========================================"

cd "$FRONTEND_DIR"

echo "==> Installing frontend dependencies..."

npm ci

echo
echo "==> Building frontend..."

rm -rf "$TEMP_FRONTEND_DIR"
mkdir -p "$TEMP_FRONTEND_DIR"

npm run build -- --outDir "$TEMP_FRONTEND_DIR"

echo
echo "==> Verifying frontend build..."

test -f "$TEMP_FRONTEND_DIR/index.html"

echo "    Frontend build OK."

#
# 4. Build backend
#
echo
echo "========================================"
echo " Building backend"
echo "========================================"

cd "$APP_DIR"

echo "==> Preparing temporary backend directory..."

rm -rf "$TEMP_API_DIR"
mkdir -p "$TEMP_API_DIR"

echo "==> Publishing .NET API..."

dotnet publish \
    "$API_PROJECT/FinanceApp.API.csproj" \
    -c Release \
    -o "$TEMP_API_DIR" \
    --no-self-contained

echo
echo "==> Verifying backend build..."

test -f "$TEMP_API_DIR/FinanceApp.API.dll"

echo "    Backend build OK."

#
# 5. Restore production configuration
#
echo
echo "==> Restoring production configuration..."

test -f "$API_PROJECT/appsettings.json"

cp \
    "$API_PROJECT/appsettings.json" \
    "$TEMP_API_DIR/appsettings.json"

echo "    appsettings.json copied."

#
# 6. Stop API before swapping backend
#
echo
echo "========================================"
echo " Installing new version"
echo "========================================"

echo "==> Stopping backend service..."

systemctl stop "$SERVICE"

#
# 7. Backup current backend
#
echo "==> Backing up current backend..."

if [[ -d "$PUBLISH_DIR" ]]; then
    mv "$PUBLISH_DIR" "$BACKUP_API_DIR"
fi

#
# 8. Install new backend
#
echo "==> Installing new backend..."

mv "$TEMP_API_DIR" "$PUBLISH_DIR"

API_SWAPPED=1

chown -R www-data:www-data "$PUBLISH_DIR"

#
# 9. Backup current frontend
#
echo "==> Backing up current frontend..."

if [[ -d "$FRONTEND_DIST" ]]; then
    mv "$FRONTEND_DIST" "$BACKUP_FRONTEND_DIR"
fi

#
# 10. Install new frontend
#
echo "==> Installing new frontend..."

mv "$TEMP_FRONTEND_DIR" "$FRONTEND_DIST"

FRONTEND_SWAPPED=1

#
# 11. Make sure Apache can read frontend
#
echo "==> Checking frontend permissions..."

chmod -R a+rX "$FRONTEND_DIST"

#
# 12. Start backend
#
echo
echo "==> Starting backend service..."

systemctl start "$SERVICE"

#
# 13. Wait for application
#
echo "==> Waiting for application..."

sleep 3

#
# 14. Check systemd
#
echo
echo "==> Checking backend service..."

if ! systemctl is-active --quiet "$SERVICE"; then
    echo
    echo "ERROR: Backend service is not running."
    systemctl --no-pager --full status "$SERVICE" || true
    exit 1
fi

echo "    Backend service is running."

#
# 15. Check backend port
#
echo
echo "==> Checking backend HTTP endpoint..."

if ! curl \
    --fail \
    --silent \
    --show-error \
    --max-time 10 \
    http://127.0.0.1:5000/ \
    >/dev/null; then

    echo
    echo "WARNING: Backend root endpoint did not return HTTP 2xx."
    echo "This may be normal if the API does not have a root endpoint."
    echo "Checking systemd status instead..."
fi

#
# 16. Check frontend through Apache
#
echo
echo "==> Checking frontend through Apache..."

if ! curl \
    --fail \
    --silent \
    --show-error \
    --max-time 10 \
    -k \
    https://127.0.0.1/financeapp/ \
    >/dev/null; then

    echo
    echo "ERROR: Frontend health check failed."
    exit 1
fi

echo "    Frontend is reachable."

#
# 17. Verify deployed files
#
echo
echo "==> Verifying deployed files..."

test -f "$PUBLISH_DIR/FinanceApp.API.dll"
test -f "$PUBLISH_DIR/appsettings.json"
test -f "$FRONTEND_DIST/index.html"

echo "    Backend files OK."
echo "    Frontend files OK."

#
# 18. Deployment successful
#
DEPLOY_FINISHED=1

echo
echo "========================================"
echo " DEPLOYMENT SUCCESSFUL"
echo "========================================"
echo

echo "Commit:"
git log -1 --oneline

echo
echo "Backend:"
echo "$PUBLISH_DIR"

echo
echo "Frontend:"
echo "$FRONTEND_DIST"

echo
echo "Backend service:"
systemctl --no-pager --full status "$SERVICE"

echo
echo "Backend backup:"
echo "$BACKUP_API_DIR"

echo
echo "Frontend backup:"
echo "$BACKUP_FRONTEND_DIR"

echo
echo "========================================"
echo " Done."
echo "========================================"
echo