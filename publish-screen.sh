#!/usr/bin/env bash
# Publish the current trmnl-server screen on the site.
# Add the nginx location /trmnl/screen.png. It goes to the local trmnl-server.
# Only this path is public: /api/setup and the other paths stay closed.
#
# Usage on the server with nginx: sudo ./publish-screen.sh SITE
#   SITE is the nginx server_name, for example example.com.
#   Set CONF if the script does not find the site file.
# Run it again safely: it does not add the same lines two times.
set -euo pipefail

if [[ $# -lt 1 ]]; then
    echo "Usage: sudo $0 SITE (the nginx server_name)" >&2
    exit 1
fi

SITE="$1"
PORT=15000
SNIPPET=/etc/nginx/snippets/trmnl-screen.conf
INCLUDE_LINE="include $SNIPPET;"
BACKUP_DIR="/root/nginx-backup/trmnl-screen-$(date +%Y%m%d-%H%M%S)"

if [[ $EUID -ne 0 ]]; then
    echo "Запустіть від root: sudo $0 $*" >&2
    exit 1
fi

echo "==> Checking the local trmnl-server"
if ! curl -fsS --max-time 5 "http://127.0.0.1:$PORT/health" >/dev/null; then
    echo "trmnl-server не відповідає на порту $PORT" >&2
    exit 1
fi

# ---------- Site file ----------

SITE_RE="${SITE//./\\.}"
SERVER_NAME_RE="server_name[^;]*[[:space:]]${SITE_RE}([[:space:];]|\$)"

if [[ -z "${CONF:-}" ]]; then
    # sites-enabled has symbolic links: edit the real file.
    mapfile -t FOUND < <(grep -RlE "$SERVER_NAME_RE" /etc/nginx/sites-enabled /etc/nginx/conf.d 2>/dev/null \
        | xargs -r -n1 readlink -f | sort -u)
    if (( ${#FOUND[@]} != 1 )); then
        echo "Знайдено ${#FOUND[@]} файлів nginx з server_name $SITE: ${FOUND[*]:-немає}" >&2
        echo "Вкажіть файл: sudo CONF=/etc/nginx/sites-available/<file> $0 $SITE" >&2
        exit 1
    fi
    CONF="${FOUND[0]}"
fi
CONF="$(readlink -f "$CONF")"
echo "==> Site file: $CONF"

mkdir -p "$BACKUP_DIR"
cp -a "$CONF" "$BACKUP_DIR/"
SNIPPET_EXISTED=0
[[ -f "$SNIPPET" ]] && SNIPPET_EXISTED=1 && cp -a "$SNIPPET" "$BACKUP_DIR/"
echo "==> Backup: $BACKUP_DIR"

restore() {
    echo "!! Restoring the backup" >&2
    cp -a "$BACKUP_DIR/$(basename "$CONF")" "$CONF"
    if (( SNIPPET_EXISTED )); then
        cp -a "$BACKUP_DIR/$(basename "$SNIPPET")" "$SNIPPET"
    else
        rm -f "$SNIPPET"
    fi
}

# ---------- nginx ----------

mkdir -p "$(dirname "$SNIPPET")"
cat > "$SNIPPET" <<EOF
# Current screen of trmnl-server. Only this path is public.
# Add ?width=400&height=300 for the compact screen.
location = /trmnl/screen.png {
    proxy_pass http://127.0.0.1:$PORT/screen.png;
    proxy_set_header Host \$host;
    proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto \$scheme;
}
EOF
chmod 644 "$SNIPPET"

if grep -qF "$INCLUDE_LINE" "$CONF"; then
    echo "==> $CONF already includes $SNIPPET"
else
    # Add the include after each server_name line of the site (for example, the blocks for ports 80 and 443).
    sed -i -E "/$SERVER_NAME_RE/a\\    $INCLUDE_LINE" "$CONF"
    echo "==> Added: $INCLUDE_LINE"
fi

if ! nginx -t; then
    restore
    exit 1
fi
systemctl reload nginx
echo "==> nginx reloaded"

# ---------- Check ----------

CODE="$(curl -sk -o /dev/null -w '%{http_code} %{content_type}' --max-time 15 \
    --resolve "$SITE:443:127.0.0.1" "https://$SITE/trmnl/screen.png" || true)"
echo "==> https://$SITE/trmnl/screen.png: $CODE"
if [[ "$CODE" != "200 image/png" ]]; then
    echo "!! Картинка не відкривається. Перевірте журнал: journalctl -u trmnl-server -n 50" >&2
    exit 1
fi
echo "==> Done"
