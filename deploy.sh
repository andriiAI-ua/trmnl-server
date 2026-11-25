#!/usr/bin/env bash
set -euo pipefail

APP=trmnl-server
ROOT=/opt/$APP
SERVICE=$APP.service

PORT=15000
HEALTH_URL="http://127.0.0.1:$PORT/health"

# ---------- Package ----------

if [[ ! -f .last_build ]]; then
    echo "No build info (.last_build)"
    exit 1
fi

PKG=$(cat .last_build)

if [[ ! -f "$PKG" ]]; then
    echo "Missing package: $PKG"
    exit 1
fi

echo "==> Using package: $PKG"

# ---------- Version ----------

VER=$(echo "$PKG" | sed -n 's/.*-v\([0-9.]*\)-.*/\1/p')

if [[ -z "$VER" ]]; then
    echo "Cannot parse version"
    exit 1
fi

TARGET="$ROOT/v$VER"

# ---------- Version check ----------

# The cleanup below removes the releases with the lowest versions.
# Thus a new release must have the highest version. Do this check before the service stops.
shopt -s nullglob
RELEASES=("$ROOT"/v*)
shopt -u nullglob

HIGHEST=$(printf '%s\n' "${RELEASES[@]##*/v}" | sort -V | tail -n 1)

if [[ -n "$HIGHEST" && "$(printf '%s\n' "$VER" "$HIGHEST" | sort -V | tail -n 1)" != "$VER" ]]; then
    echo "!! v$VER is lower than the installed v$HIGHEST"
    echo "!! Run ./build.sh again: it continues from the highest version"
    exit 1
fi

# ---------- Prev ----------

PREV=""

if [[ -L "$ROOT/current" ]]; then
    PREV=$(readlink "$ROOT/current")
    PREV="$ROOT/$(basename "$PREV")"
fi

# ---------- Stop ----------

if systemctl is-active --quiet "$SERVICE"; then
    systemctl stop "$SERVICE"
fi

# ---------- Install ----------

echo "==> Installing v$VER → $TARGET"

mkdir -p "$TARGET"

tar -xzf "$PKG" -C "$TARGET"

# ---------- Data ----------

mkdir -p "$ROOT/data"

rm -rf "$TARGET/Data"
ln -s "$ROOT/data" "$TARGET/Data"

# ---------- Screens ----------

mkdir -p "$ROOT/screens"

rm -rf "$TARGET/wwwroot/screens"
mkdir -p "$TARGET/wwwroot/"
ln -s "$ROOT/screens" "$TARGET/wwwroot/screens"

# ---------- Firmware ----------

# The firmware builds are in git (wwwroot/firmware) and come with the release.
# Do not link this folder to a shared folder.

# ---------- Switch ----------

ln -sfn "$TARGET" "$ROOT/current"

# ---------- Restart ----------

UNIT_FILE="/etc/systemd/system/$SERVICE"

if [[ ! -f "$UNIT_FILE" ]]; then
    echo "==> Creating systemd unit: $SERVICE"

    cat > "$UNIT_FILE" <<EOF
[Unit]
Description=TRMNL Server
After=network.target

[Service]
Type=simple

WorkingDirectory=$ROOT/current

ExecStart=$ROOT/current/$APP --urls http://0.0.0.0:15000

Restart=always
RestartSec=3

User=root
Group=root

Environment=DOTNET_ENVIRONMENT=Production

StandardOutput=journal
StandardError=journal

LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
EOF
fi



systemctl daemon-reload
systemctl restart "$SERVICE"

# ---------- Health ----------

echo "==> Health check"

OK=0

for i in {1..10}; do
    if curl -fs "$HEALTH_URL" >/dev/null; then
        OK=1
        break
    fi

    sleep 1
done

if [[ "$OK" != "1" ]]; then
    echo "!! Health failed"

    if [[ -n "$PREV" ]]; then
        echo "Rollback → $PREV"
        ln -sfn "$PREV" "$ROOT/current"
        systemctl restart "$SERVICE"
    fi

    exit 1
fi

echo "==> Health OK"

# ---------- Cleanup ----------

cd "$ROOT"

VERS=$(ls -d v* 2>/dev/null | sort -V)

CNT=$(echo "$VERS" | wc -l)

BETA=$(readlink -f "$ROOT/beta" 2>/dev/null || true)

if (( CNT > 3 )); then
    for d in $(echo "$VERS" | head -n $((CNT - 3))); do
        # Do not remove the new release, the previous release or the beta release.
        if [[ "$ROOT/$d" == "$TARGET" || "$ROOT/$d" == "$PREV" || "$ROOT/$d" == "$BETA" ]]; then
            echo "==> Keep $d"
            continue
        fi

        rm -rf "$d"
    done
fi

echo "==> Deploy done v$VER"
