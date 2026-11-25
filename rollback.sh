#!/usr/bin/env bash
set -euo pipefail

APP=trmnl-server
ROOT=/opt/$APP
SERVICE=$APP.service

cd "$ROOT"

VERSIONS=$(ls -d v* | sort -V)

COUNT=$(echo "$VERSIONS" | wc -l)

if (( COUNT < 2 )); then
    echo "No previous version"
    exit 1
fi

PREV=$(echo "$VERSIONS" | tail -n 2 | head -n 1)

echo "Rollback → $PREV"

ln -sfn "$ROOT/$PREV" "$ROOT/current"

systemctl restart "$SERVICE"

echo "Done"
