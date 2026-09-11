#!/usr/bin/env bash
# deploy-pr77.sh — Deploy current origin/main (includes PR #77) to the production server.
#
# Assumptions:
#   - Repo clone: /var/FinanceApp
#   - Frontend web root: /var/www/html/financeapp
#   - A running systemd service whose name contains "financeapp" (case-insensitive)
#   - .NET SDK ≥ 8, Node.js ≥ 20, npm ≥ 8 are available
#   - The committed package-lock.json contains esbuild 0.25.x entries (Vite 6 compatible)

set -Eeuo pipefail

REPO="/var/FinanceApp"
FRONTEND_TARGET="/var/www/html/financeapp"
MERGE_COMMIT="a2ac62d79085c4d0bbfaf6808a7bf40b6d08bbbd"

TIMESTAMP="$(date +%Y%m%d-%H%M%S)"
WORKTREE="/root/financeapp-pr77-${TIMESTAMP}"
PUBLISH_DIR="${WORKTREE}/.publish-api"

SERVICE=""
API_TARGET=""
API_BACKUP=""
FRONTEND_BACKUP=""
DEPLOY_STARTED=0

# ---------------------------------------------------------------------------
cleanup() {
  cd /root

  if [ -d "$WORKTREE" ]; then
    git -C "$REPO" worktree remove --force "$WORKTREE" 2>/dev/null \
      || rm -rf "$WORKTREE"
  fi

  git -C "$REPO" worktree prune 2>/dev/null || true
}

rollback() {
  local exit_code=$?

  if [ "$exit_code" -ne 0 ] && [ "$DEPLOY_STARTED" = "1" ]; then
    echo
    echo "=== DEPLOYMENT FAILED (exit $exit_code): ROLLING BACK ==="

    if [ -n "$SERVICE" ]; then
      systemctl stop "$SERVICE" 2>/dev/null || true
    fi

    if [ -n "$API_BACKUP" ] && [ -n "$API_TARGET" ] && [ -d "$API_BACKUP" ]; then
      rsync -a --delete "$API_BACKUP/" "$API_TARGET/"
    fi

    if [ -n "$FRONTEND_BACKUP" ] && [ -d "$FRONTEND_BACKUP" ]; then
      mkdir -p "$FRONTEND_TARGET"
      rsync -a --delete "$FRONTEND_BACKUP/" "$FRONTEND_TARGET/"
      chown -R www-data:www-data "$FRONTEND_TARGET"
    fi

    if [ -n "$SERVICE" ]; then
      systemctl start "$SERVICE" || true
      echo
      systemctl status "$SERVICE" --no-pager --lines=50 || true
      echo
      journalctl -u "$SERVICE" -n 100 --no-pager || true
    fi
  fi

  cleanup
  exit "$exit_code"
}

trap rollback EXIT

# ---------------------------------------------------------------------------
echo "=== CHECK DIRECTORIES ==="
test -d "$REPO"    || { echo "ERROR: repo directory $REPO not found"; exit 1; }
test -d "$FRONTEND_TARGET" \
  || { echo "ERROR: frontend target $FRONTEND_TARGET not found"; exit 1; }

echo
echo "=== FETCH MAIN ==="
git -C "$REPO" fetch --prune origin main

git -C "$REPO" merge-base --is-ancestor "$MERGE_COMMIT" origin/main || {
  echo "ERROR: PR #77 merge commit ($MERGE_COMMIT) is not in origin/main."
  exit 1
}

git -C "$REPO" worktree prune
git -C "$REPO" worktree add --detach "$WORKTREE" origin/main

echo
echo "=== SOURCE COMMIT ==="
git -C "$WORKTREE" log -1 --oneline
ACTUAL_COMMIT="$(git -C "$WORKTREE" rev-parse HEAD)"
echo "HEAD=$ACTUAL_COMMIT"

echo
echo "=== VERIFY PR #77 SOURCE ==="
grep -Fq '[HttpPut("{id}/metadata")]' \
  "$WORKTREE/FinanceApp.API/Controllers/StocksController.cs"
grep -Fq 'StatusCodes.Status410Gone' \
  "$WORKTREE/FinanceApp.API/Controllers/StocksController.cs"
grep -Fq '[HttpPatch("{id}/quote")]' \
  "$WORKTREE/FinanceApp.API/Controllers/StocksController.cs"
grep -Fq 'UpdateStockMetadataRequest' \
  "$WORKTREE/FinanceApp.API/Models/CurrencyAwareStockDtos.cs"
grep -Fq 'updateStockMetadata' \
  "$WORKTREE/FinanceApp.Frontend/src/services/api.ts"
grep -Fq 'StockExchangeTag' \
  "$WORKTREE/FinanceApp.Frontend/src/pages/PortfolioDetailPage.tsx"
grep -Fq 'GET, POST, PUT, PATCH, DELETE, OPTIONS' \
  "$WORKTREE/FinanceApp.API/Program.cs"

if grep -Fq 'await updateStock(editingStock.id' \
    "$WORKTREE/FinanceApp.Frontend/src/pages/StocksPage.tsx"; then
  echo "ERROR: frontend still calls legacy updateStock() — wrong source."
  exit 1
fi

echo "PR #77 source verification passed."

echo
echo "=== DISCOVER RUNNING API SERVICE ==="
SERVICE="$(
  systemctl list-units \
    --type=service \
    --state=running \
    --no-legend \
  | awk 'tolower($1) ~ /financeapp/ { print $1; exit }'
)"

if [ -z "$SERVICE" ]; then
  echo "ERROR: no running systemd service whose name contains 'financeapp' was found."
  systemctl list-units --type=service --all | grep -Ei 'finance|dotnet' || true
  exit 1
fi

MAIN_PID="$(systemctl show "$SERVICE" -p MainPID --value)"
if [ -z "$MAIN_PID" ] || [ "$MAIN_PID" = "0" ]; then
  echo "ERROR: $SERVICE has no running process (MainPID=0)."
  systemctl status "$SERVICE" --no-pager || true
  exit 1
fi

# Derive API deployment directory from the running process command line.
# Fall back to systemd ExecStart if /proc/PID/cmdline does not contain the DLL
# path (e.g. the file was deleted from disk by a prior git update).
API_DLL="$(
  tr '\0' '\n' < "/proc/$MAIN_PID/cmdline" \
  | grep '/FinanceApp.API\.dll$' \
  | head -1
)"

if [ -z "$API_DLL" ]; then
  echo "WARNING: DLL path not found in process cmdline; falling back to systemd ExecStart."
  API_DLL="$(
    systemctl show "$SERVICE" -p ExecStart --value \
    | grep -oP '[^ ]+/FinanceApp\.API\.dll'
  )"
fi

if [ -z "$API_DLL" ]; then
  echo "ERROR: cannot determine FinanceApp.API.dll path from process $MAIN_PID or systemd ExecStart."
  echo "Process command:"
  tr '\0' ' ' < "/proc/$MAIN_PID/cmdline"
  echo
  systemctl show "$SERVICE" -p ExecStart --value || true
  exit 1
fi

API_TARGET="$(dirname "$API_DLL")"
# Note: do NOT require the DLL to exist on disk here — a prior git update or
# cleanup may have removed it before this deployment script ran.  The directory
# itself must exist (it is where we will place the new publish).
test -d "$API_TARGET" \
  || { echo "ERROR: API directory $API_TARGET does not exist"; exit 1; }

echo "SERVICE=$SERVICE"
echo "API_TARGET=$API_TARGET"

echo
echo "=== TOOL VERSIONS ==="
dotnet --version
node  --version
npm   --version

DOTNET_MAJOR="$(dotnet --version | cut -d. -f1)"
NODE_MAJOR="$(node -p 'Number(process.versions.node.split(".")[0])')"

if [ "$DOTNET_MAJOR" -lt 8 ]; then
  echo "ERROR: .NET SDK 8 or newer is required (found $DOTNET_MAJOR)."
  exit 1
fi

if [ "$NODE_MAJOR" -lt 20 ]; then
  echo "ERROR: Node.js 20 or newer is required (found $NODE_MAJOR)."
  exit 1
fi

cd "$WORKTREE"

echo
echo "=== RESTORE BACKEND ==="
dotnet restore FinanceApp.sln

echo
echo "=== BACKEND TESTS ==="
dotnet test \
  FinanceApp.Core.Tests/FinanceApp.Core.Tests.csproj \
  -c Release \
  --no-restore

echo
echo "=== API RELEASE BUILD ==="
dotnet build \
  FinanceApp.API/FinanceApp.API.csproj \
  -c Release \
  --no-restore

echo
echo "=== PUBLISH API ==="
rm -rf "$PUBLISH_DIR"
dotnet publish \
  FinanceApp.API/FinanceApp.API.csproj \
  -c Release \
  --no-restore \
  -o "$PUBLISH_DIR"

test -f "$PUBLISH_DIR/FinanceApp.API.dll"
test -f "$PUBLISH_DIR/FinanceApp.API.deps.json"
test -f "$PUBLISH_DIR/FinanceApp.API.runtimeconfig.json"

echo
echo "=== FRONTEND INSTALL ==="
# Use the committed lockfile exactly; do NOT delete or regenerate it.
cd "$WORKTREE/FinanceApp.Frontend"
rm -rf node_modules
npm ci --include=optional --no-audit --no-fund

echo
echo "=== FRONTEND TESTS ==="
npm test

echo
echo "=== FRONTEND PRODUCTION BUILD ==="
npm run build

test -f dist/index.html
test -d dist/assets

grep -rFl '/metadata' dist/assets > /dev/null
grep -rFl '/quote'    dist/assets > /dev/null

echo
echo "=== CREATE BACKUPS ==="
API_BACKUP="${API_TARGET}-backup-pr77-${TIMESTAMP}"
FRONTEND_BACKUP="/var/www/html/financeapp-backup-pr77-${TIMESTAMP}"

mkdir -p "$API_BACKUP"
rsync -a "$API_TARGET/" "$API_BACKUP/"

mkdir -p "$FRONTEND_BACKUP"
rsync -a "$FRONTEND_TARGET/" "$FRONTEND_BACKUP/"

test -f "$API_BACKUP/FinanceApp.API.dll"
test -f "$FRONTEND_BACKUP/index.html"

cat > /root/financeapp-pr77-backup.env <<EOF
PR_NUMBER=77
MERGE_COMMIT=${MERGE_COMMIT}
DEPLOYED_SOURCE_COMMIT=${ACTUAL_COMMIT}
DEPLOYED_AT=${TIMESTAMP}
SERVICE=${SERVICE}
API_TARGET=${API_TARGET}
API_BACKUP=${API_BACKUP}
FRONTEND_TARGET=${FRONTEND_TARGET}
FRONTEND_BACKUP=${FRONTEND_BACKUP}
EOF

echo "API_BACKUP=$API_BACKUP"
echo "FRONTEND_BACKUP=$FRONTEND_BACKUP"

DEPLOY_STARTED=1

echo
echo "=== DEPLOY API ==="
systemctl stop "$SERVICE"

# Preserve production configuration and runtime-generated directories.
rsync -a --delete \
  --exclude='appsettings.json' \
  --exclude='appsettings.Production.json' \
  --exclude='logs/' \
  --exclude='uploads/' \
  "$PUBLISH_DIR/" \
  "$API_TARGET/"

systemctl start "$SERVICE"

echo
echo "=== WAIT FOR API ==="
sleep 5

if ! systemctl is-active --quiet "$SERVICE"; then
  echo "ERROR: API service failed to start after deployment."
  systemctl status "$SERVICE" --no-pager --lines=100 || true
  journalctl -u "$SERVICE" -n 150 --no-pager || true
  exit 1
fi

echo
echo "=== DEPLOY FRONTEND ==="
rsync -a --delete \
  "$WORKTREE/FinanceApp.Frontend/dist/" \
  "$FRONTEND_TARGET/"

chown -R www-data:www-data "$FRONTEND_TARGET"
find "$FRONTEND_TARGET" -type d -exec chmod 755 {} \;
find "$FRONTEND_TARGET" -type f -exec chmod 644 {} \;

test -f "$FRONTEND_TARGET/index.html"

echo
echo "=== FINAL VERIFICATION ==="
systemctl is-active --quiet "$SERVICE"
systemctl status "$SERVICE" --no-pager --lines=30

grep -rFl '/metadata' "$FRONTEND_TARGET/assets" > /dev/null
grep -rFl '/quote'    "$FRONTEND_TARGET/assets" > /dev/null

echo
echo "=== RECENT API LOGS ==="
journalctl -u "$SERVICE" -n 60 --no-pager

DEPLOY_STARTED=0

echo
echo "========================================"
echo "PR #77 DEPLOYED SUCCESSFULLY"
echo "Commit  : $ACTUAL_COMMIT"
echo "Service : $SERVICE"
echo "API     : $API_TARGET"
echo "Frontend: $FRONTEND_TARGET"
echo "Backups : /root/financeapp-pr77-backup.env"
echo "========================================"
