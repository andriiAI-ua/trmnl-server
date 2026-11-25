#!/usr/bin/env bash
# Move the local settings of this server from appsettings.json of a release to Data/appsettings.local.json.
# appsettings.json in git has only empty values. The real values (Home Assistant, MQTT, the sensors,
# the forecast place) must be in the shared data folder: then a new release keeps them.
#
# Run it once on each server, before the deploy of the first release with the empty appsettings.json:
#   cd <checkout of trmnl-server> && git pull && sudo ./move-settings-to-data.sh
#
# The script reads the newest release that has the values (or the file in the first argument).
# It adds only the keys that Data/appsettings.local.json does not have, so it does not change your values.
# It writes the file for the service (data/) and for the beta (Data/), if the folder is there.
# You can run it again: when all keys are there, it changes nothing.
set -euo pipefail

APP=trmnl-server
ROOT=/opt/$APP

if [[ $EUID -ne 0 ]]; then
    echo "Run it as root: sudo $0 $*" >&2
    exit 1
fi

if ! command -v python3 >/dev/null 2>&1; then
    echo "python3 not found" >&2
    exit 1
fi

# The newest releases first: the current release, then the others by version.
SOURCES=()
if [[ $# -ge 1 ]]; then
    SOURCES=("$1")
else
    if [[ -f "$ROOT/current/appsettings.json" ]]; then
        SOURCES+=("$ROOT/current/appsettings.json")
    fi
    while IFS= read -r d; do
        SOURCES+=("$d/appsettings.json")
    done < <(ls -d "$ROOT"/v* 2>/dev/null | sort -rV)
fi

TARGETS=()
for d in "$ROOT/data" "$ROOT/Data"; do
    if [[ -d "$d" ]]; then
        TARGETS+=("$d/appsettings.local.json")
    fi
done

if (( ${#TARGETS[@]} == 0 )); then
    echo "No data folder in $ROOT" >&2
    exit 1
fi

python3 - "${#SOURCES[@]}" "${SOURCES[@]}" "${TARGETS[@]}" <<'PYTHON'
import json
import os
import sys

# The settings of this server. The other settings keep the values of appsettings.json in git.
KEYS = [
    ('HaMqtt', 'Broker'), ('HaMqtt', 'Port'), ('HaMqtt', 'Username'), ('HaMqtt', 'Password'),
    ('HomeAssistant', 'Url'), ('HomeAssistant', 'Token'),
    ('Dashboard', 'PowerQueue'), ('Dashboard', 'Temperature'), ('Dashboard', 'Humidity'),
    ('Dashboard', 'AirQuality'), ('Dashboard', 'Voltage'), ('Dashboard', 'DroneAlert'),
    ('Dashboard', 'MissileAlert'), ('Dashboard', 'UpsStatus'), ('Dashboard', 'NextPowerOff'),
    ('Dashboard', 'NextPowerOn'), ('Dashboard', 'ForecastStation'), ('Dashboard', 'ForecastCity'),
    ('Dashboard', 'ForecastRegion'), ('Dashboard', 'RoomTemperature'), ('Dashboard', 'RoomHumidity'),
]

count = int(sys.argv[1])
sources = sys.argv[2:2 + count]
targets = sys.argv[2 + count:]


def read(path):
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f)


def get(data, section, key):
    value = data.get(section, {}).get(key)
    return None if value in (None, '', []) else value


# Use the newest file with a real value. An empty appsettings.json of a new release has no values.
source, values = None, {}
for path in sources:
    if not os.path.isfile(path):
        continue
    data = read(path)
    found = {k: get(data, *k) for k in KEYS if get(data, *k) is not None}
    if any(section != 'HaMqtt' or key != 'Port' for section, key in found):
        source, values = path, found
        break

if source is None:
    print('No release has the local settings. Write Data/appsettings.local.json by hand.')
    sys.exit(0)
print('==> Settings from', source)

for target in targets:
    local = read(target) if os.path.isfile(target) else {}
    added = []
    for (section, key), value in values.items():
        if key not in local.get(section, {}):
            local.setdefault(section, {})[key] = value
            added.append(section + ':' + key)
    if not added:
        print('==> %s has all the settings' % target)
        continue
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
    print('==> %s: added %s' % (target, ', '.join(added)))
PYTHON
