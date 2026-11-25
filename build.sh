#!/usr/bin/env bash
set -euo pipefail

APP=trmnl-server

VERSION_FILE=VERSION

# ---------- Arch ----------

ARCH=$(uname -m)

case "$ARCH" in
    aarch64) RID="linux-arm64" ;;
    armv7l)  RID="linux-arm" ;;
    x86_64)  RID="linux-x64" ;;
    *)
        echo "Unsupported arch: $ARCH"
        exit 1
        ;;
esac

echo "==> Arch: $ARCH → $RID"

# ---------- Version ----------

if [[ ! -f $VERSION_FILE ]]; then
    echo "0.0.0" > "$VERSION_FILE"
fi

# The VERSION file can go back, for example after "git checkout VERSION".
# Then deploy.sh can remove the new release as the oldest release.
# Thus start from the highest version of the VERSION file, the packages and the installed releases.
VERSIONS=("$(tr -d '[:space:]' < "$VERSION_FILE")")

shopt -s nullglob
for f in build-v*.tar.gz /opt/$APP/v*; do
    v=$(basename "$f")
    v=${v#build-}
    v=${v#v}
    v=${v%%-*}
    if [[ $v =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
        VERSIONS+=("$v")
    fi
done
shopt -u nullglob

OLD=$(printf '%s\n' "${VERSIONS[@]}" | sort -V | tail -n 1)

if [[ "$OLD" != "${VERSIONS[0]}" ]]; then
    echo "==> VERSION has ${VERSIONS[0]}, but v$OLD exists: continue from v$OLD"
fi

IFS='.' read -r MAJ MIN PATCH <<< "$OLD"

PATCH=$((PATCH + 1))

NEW="$MAJ.$MIN.$PATCH"

echo "$NEW" > "$VERSION_FILE"

echo "==> Version: v$NEW"

# ---------- Fonts ----------

FONT_DIR="Fonts"
FONT_FILE="Roboto-Regular.ttf"
FONT_PATH="$FONT_DIR/$FONT_FILE"

FONT_URL="https://raw.githubusercontent.com/googlefonts/roboto-2/main/src/hinted/Roboto-Regular.ttf"

mkdir -p "$FONT_DIR"

if [[ ! -f "$FONT_PATH" ]]; then
    echo "==> Downloading font: $FONT_FILE"

    if ! command -v wget >/dev/null 2>&1; then
        echo "wget not found"
        exit 1
    fi

    wget -O "$FONT_PATH" "$FONT_URL"

    if [[ ! -s "$FONT_PATH" ]]; then
        echo "Font download failed"
        exit 1
    fi
else
    echo "==> Font OK"
fi

BOLD_FILE="Roboto-Bold.ttf"
BOLD_URL="https://raw.githubusercontent.com/googlefonts/roboto-2/main/src/hinted/Roboto-Bold.ttf"

if [[ ! -f "$FONT_DIR/$BOLD_FILE" ]]; then
    wget -O "$FONT_DIR/$BOLD_FILE" "$BOLD_URL"
fi

# ---------- Clean ----------

dotnet clean
rm -rf bin obj

# ---------- Publish ----------

dotnet publish \
    -c Release \
    -r "$RID" \
    --self-contained true

OUT="bin/Release/net10.0/$RID/publish"

if [[ ! -d "$OUT" ]]; then
    echo "Publish failed"
    exit 1
fi

# ---------- Pack ----------

PKG="build-v$NEW-$RID.tar.gz"

echo "==> Packing $PKG"

tar -C "$OUT" -czf "$PKG" .

echo "$PKG" > .last_build

echo "==> Build OK"
