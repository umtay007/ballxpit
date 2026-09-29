#!/usr/bin/env bash
# Packs a mod zip into the one-click installer.
#
#   installer/build-installer.sh <mod zip> <version> <output .bat>
set -euo pipefail
ZIP="$1"
VERSION="$2"
OUT="$3"
TEMPLATE="$(dirname "$0")/Install.bat.template"
PAYLOAD="$(mktemp)"
trap 'rm -f "$PAYLOAD"' EXIT
base64 < "$ZIP" | tr -d '\n' | fold -w 76 > "$PAYLOAD"
# cmd.exe wants Windows line endings.
awk -v version="$VERSION" -v payload="$PAYLOAD" '
  { sub(/\r$/, "") }
  $0 == "@@PAYLOAD@@" { while ((getline line < payload) > 0) printf "%s\r\n", line; next }
  { gsub(/@@VERSION@@/, version); printf "%s\r\n", $0 }
' "$TEMPLATE" > "$OUT"
