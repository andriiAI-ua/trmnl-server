#!/usr/bin/env bash
set -euo pipefail

PORT=15000
# The server to check. Default: this computer.
HOST=${1:-127.0.0.1}

echo "QA warmup..."

curl -s \
  -H "ID: qa-probe" \
  -H "Access-Token: qa" \
  "http://$HOST:$PORT/api/display"  | jq

echo "Done"
