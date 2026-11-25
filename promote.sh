#!/usr/bin/env bash
set -euo pipefail


# -------------------------------
# Paths
# -------------------------------

APP=trmnl-server
ROOT=/opt/$APP

PROD_SERVICE=$APP.service
BETA_LINK=$ROOT/beta
PROD_LINK=$ROOT/current


# -------------------------------
# Checks
# -------------------------------

if [[ ! -L "$BETA_LINK" ]]; then
    echo "Beta link not found: $BETA_LINK"
    exit 1
fi

TARGET="$(readlink -f "$BETA_LINK")"

if [[ ! -d "$TARGET" ]]; then
    echo "Invalid beta target: $TARGET"
    exit 1
fi


echo "==> Promote: $TARGET → production"


# -------------------------------
# Switch prod
# -------------------------------

ln -sfn "$TARGET" "$PROD_LINK"


# -------------------------------
# Restart prod
# -------------------------------

systemctl restart "$PROD_SERVICE"


echo "==> Production now running: $(basename "$TARGET")"
