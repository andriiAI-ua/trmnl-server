#!/usr/bin/env bash
set -euo pipefail


# -------------------------------
# Paths
# -------------------------------

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

APP=trmnl-server
ROOT=/opt/$APP

SERVICE=$APP-beta.service
PORT=15001


# -------------------------------
# Shared dirs
# -------------------------------

# The firmware builds are in git (wwwroot/firmware) and come with the release.
SHARED=(
  Data
  Fonts
)


# -------------------------------
# Read last build
# -------------------------------

LAST_BUILD="$SCRIPT_DIR/.last_build"

if [[ ! -f "$LAST_BUILD" ]]; then
    echo "No build info ($LAST_BUILD missing)"
    exit 1
fi

PKG_NAME=$(cat "$LAST_BUILD")
PKG_PATH="$SCRIPT_DIR/$PKG_NAME"

if [[ ! -f "$PKG_PATH" ]]; then
    echo "Build package not found: $PKG_PATH"
    exit 1
fi

VER=$(echo "$PKG_NAME" | sed -n 's/.*-v\([0-9.]*\)-.*/\1/p')

if [[ -z "$VER" ]]; then
    echo "Cannot parse version from: $PKG_NAME"
    exit 1
fi

TARGET="$ROOT/v$VER"


echo "==> Beta deploy v$VER"


# -------------------------------
# Install version if missing
# -------------------------------

if [[ ! -d "$TARGET" ]]; then
    echo "==> Installing $TARGET"

    mkdir -p "$TARGET"

    tar -xzf "$PKG_PATH" -C "$TARGET"
fi


# -------------------------------
# Prepare shared storage
# -------------------------------

for d in "${SHARED[@]}"; do

    mkdir -p "$ROOT/$d"

    rm -rf "$TARGET/$d"

    ln -s "$ROOT/$d" "$TARGET/$d"

done


# -------------------------------
# Symlink beta
# -------------------------------

ln -sfn "$TARGET" "$ROOT/beta"


# -------------------------------
# systemd unit
# -------------------------------

UNIT="/etc/systemd/system/$SERVICE"

if [[ ! -f "$UNIT" ]]; then

cat > "$UNIT" <<EOF
[Unit]
Description=TRMNL Server (BETA)
After=network.target

[Service]
Type=simple

WorkingDirectory=$ROOT/beta

ExecStart=$ROOT/beta/$APP --urls http://0.0.0.0:$PORT

Restart=always
RestartSec=3

Environment=TRMNL_FONT_DIR=$ROOT/Fonts

StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
EOF

    systemctl daemon-reload
    systemctl enable "$SERVICE"
fi


# -------------------------------
# Restart
# -------------------------------

systemctl restart "$SERVICE"


echo "==> Beta running on :$PORT"
