#!/usr/bin/env bash
# Write the Home Assistant URL and token for the dashboard, then restart the service.
# The file is Data/appsettings.local.json in the shared data folder, so a new release keeps it.
# The script changes only HomeAssistant:Url and HomeAssistant:Token. It keeps the other settings of the file.
#
# Usage: sudo ./configure-ha.sh [HA_URL]
#   HA_URL is for example http://homeassistant.local:8123.
#   If HA_ENV names a file, the script reads HA_URL and HA_TOKEN from it (lines HA_URL=... and HA_TOKEN=...).
#   Otherwise it asks for the token.
set -euo pipefail

APP=trmnl-server
ROOT=/opt/$APP
TARGET="$ROOT/data/appsettings.local.json"

HA_URL="${1:-}"
HA_TOKEN=""

if [[ -n "${HA_ENV:-}" ]]; then
    if [[ ! -r "$HA_ENV" ]]; then
        echo "Cannot read $HA_ENV" >&2
        exit 1
    fi
    echo "==> Reading HA_URL and HA_TOKEN from $HA_ENV"
    # shellcheck disable=SC1090
    source "$HA_ENV"
    HA_URL="${1:-${HA_URL:-}}"
fi

if [[ -z "$HA_URL" ]]; then
    read -rp "Home Assistant URL (for example http://homeassistant.local:8123): " HA_URL
fi

if [[ -z "$HA_TOKEN" ]]; then
    read -rsp "Home Assistant long-lived token: " HA_TOKEN
    echo
fi

# A token is a JWT: only these characters are permitted.
if [[ ! "$HA_TOKEN" =~ ^[A-Za-z0-9._-]+$ ]]; then
    echo "Token has unexpected characters" >&2
    exit 1
fi

if [[ ! "$HA_URL" =~ ^https?://[A-Za-z0-9.:/_-]+$ ]]; then
    echo "Incorrect URL: $HA_URL" >&2
    exit 1
fi

echo "==> Checking $HA_URL"
if ! curl -fsS --max-time 10 -H "Authorization: Bearer $HA_TOKEN" "${HA_URL%/}/api/" >/dev/null; then
    echo "Home Assistant does not accept the token at $HA_URL" >&2
    exit 1
fi

if ! command -v python3 >/dev/null 2>&1; then
    echo "python3 not found" >&2
    exit 1
fi

mkdir -p "$ROOT/data"

# Give the values in the environment, not on the command line: then "ps" does not show the token.
HA_URL="$HA_URL" HA_TOKEN="$HA_TOKEN" python3 - "$TARGET" <<'PYTHON'
import json
import os
import sys

target = sys.argv[1]
local = {}
if os.path.isfile(target):
    with open(target, encoding='utf-8-sig') as f:
        local = json.load(f)

section = local.setdefault('HomeAssistant', {})
section['Url'] = os.environ['HA_URL']
section['Token'] = os.environ['HA_TOKEN']

tmp = target + '.tmp'
old_umask = os.umask(0o077)
try:
    with open(tmp, 'w', encoding='utf-8') as f:
        json.dump(local, f, ensure_ascii=False, indent=2)
        f.write('\n')
finally:
    os.umask(old_umask)
os.chmod(tmp, 0o600)
os.replace(tmp, target)
PYTHON

echo "==> Wrote $TARGET"

systemctl restart "$APP.service"
echo "==> Restarted $APP.service"
