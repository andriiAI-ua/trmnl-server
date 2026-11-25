#!/usr/bin/env bash
set -euo pipefail

PORT=15001
HOST=127.0.0.1

echo "QA warmup..."

curl -s -H "ID: qa-probe" -H "Access-Token: qa" "http://$HOST:$PORT/api/display" | jq

echo "Done"
