#!/usr/bin/env bash
# Upload a Thunderstore-compatible package ZIP to Hexium.
#
# Adapted from jhsveli/hexium-uploader (MIT, Copyright (c) 2026 Hexium Upload
# contributors). Hexium mirrors Thunderstore's experimental upload API:
#   initiate-upload -> PUT presigned parts -> finish-upload -> submit
#
# Required environment:
#   HEXIUM_TOKEN       API token from Hexium Settings -> API Tokens
#   HEXIUM_FILE        package zip (or pass it as the first argument)
# Optional:
#   HEXIUM_NAMESPACE   team/namespace (default: BootstrapBoys)
#   HEXIUM_COMMUNITY   community slug (default: valheim)
#   HEXIUM_HOST        host (default: hexium.gg)
#   HEXIUM_BASE_URL    full API base override (testing)
#   HEXIUM_CATEGORIES  comma-separated categories
#   HEXIUM_NSFW        true/false (default: false)
#   GITHUB_OUTPUT      append package/version outputs when set
set -euo pipefail

: "${HEXIUM_TOKEN:?HEXIUM_TOKEN is required}"
FILE="${HEXIUM_FILE:-${1:-}}"
: "${FILE:?HEXIUM_FILE or a package path argument is required}"
[ -f "$FILE" ] || { echo "package not found: $FILE" >&2; exit 1; }
command -v jq >/dev/null 2>&1 || { echo "jq is required" >&2; exit 1; }

NAMESPACE="${HEXIUM_NAMESPACE:-BootstrapBoys}"
COMMUNITY="${HEXIUM_COMMUNITY:-valheim}"
HOST="${HEXIUM_HOST:-hexium.gg}"
CATEGORIES="${HEXIUM_CATEGORIES:-}"
NSFW="${HEXIUM_NSFW:-false}"
TOKEN="$HEXIUM_TOKEN"

# Accept either a raw token or an already-prefixed Bearer token.
if [[ "$TOKEN" != *" "* ]]; then
  TOKEN="Bearer $TOKEN"
fi

if [ -n "${HEXIUM_BASE_URL:-}" ]; then
  BASE="${HEXIUM_BASE_URL%/}"
else
  BASE="https://${COMMUNITY}.${HOST}"
fi

FILENAME="$(basename "$FILE")"
SIZE="$(wc -c < "$FILE" | tr -d '[:space:]')"

case "${NSFW,,}" in
  true|1|yes|on) NSFW_JSON=true ;;
  *) NSFW_JSON=false ;;
esac

tmpfile() {
  mktemp "$WORK/part.XXXXXX"
}

WORK="$(mktemp -d "${TMPDIR:-/tmp}/hexium.XXXXXX")"
cleanup() {
  rm -rf "$WORK"
}
trap cleanup EXIT

api_post() {
  local url="$1" data="$2" body headers code
  headers="$(tmpfile)"
  body="$(curl -sS -X POST "$url" \
    -H "Authorization: $TOKEN" \
    -H "Content-Type: application/json" \
    -d "$data" -D "$headers")"
  code="$(awk 'NR==1 { print $2 }' "$headers")"
  if [ -z "$code" ] || [ "$code" -ge 400 ]; then
    echo "POST $url -> HTTP ${code:-?}: $body" >&2
    return 1
  fi
  printf '%s' "$body"
}

echo "==> initiate upload: $FILENAME ($SIZE bytes) -> $BASE"
INITIATE_BODY="$(jq -n --arg filename "$FILENAME" --argjson size "$SIZE" \
  '{filename:$filename,file_size_bytes:$size}')"
INITIATE="$(api_post "$BASE/api/experimental/usermedia/initiate-upload/" "$INITIATE_BODY")"

UUID="$(jq -r '.user_media.uuid // empty' <<<"$INITIATE")"
PARTS="$(jq -r '.upload_urls | length' <<<"$INITIATE")"
[ -n "$UUID" ] || { echo "initiate-upload missing user_media.uuid: $INITIATE" >&2; exit 1; }
[ "$PARTS" -ge 1 ] 2>/dev/null || { echo "initiate-upload returned no parts: $INITIATE" >&2; exit 1; }
echo "    uuid=$UUID parts=$PARTS"

abort_upload() {
  api_post "$BASE/api/experimental/usermedia/$UUID/abort-upload/" '{}' >/dev/null 2>&1 || true
}

ETAGS=()
PNUMS=()
for ((i = 0; i < PARTS; i++)); do
  part_number="$(jq -r ".upload_urls[$i].part_number" <<<"$INITIATE")"
  offset="$(jq -r ".upload_urls[$i].offset" <<<"$INITIATE")"
  length="$(jq -r ".upload_urls[$i].length" <<<"$INITIATE")"
  url="$(jq -r ".upload_urls[$i].url" <<<"$INITIATE")"

  if [ "$offset" -eq 0 ] && [ "$length" -eq "$SIZE" ]; then
    part_file="$FILE"
  else
    part_file="$(tmpfile)"
    if ! dd if="$FILE" of="$part_file" bs=1 skip="$offset" count="$length" 2>/dev/null; then
      echo "failed to extract part $part_number" >&2
      abort_upload
      exit 1
    fi
  fi

  headers="$(tmpfile)"
  if ! curl -sS -f -X PUT --data-binary @"$part_file" -D "$headers" -o /dev/null "$url"; then
    echo "PUT part $part_number failed" >&2
    abort_upload
    exit 1
  fi

  etag="$(tr -d '\r' < "$headers" | grep -i '^etag:' | head -1 | sed 's/^[Ee][Tt][Aa][Gg]:[[:space:]]*//')"
  etag="${etag#\"}"
  etag="${etag%\"}"
  [ -n "$etag" ] || { echo "part $part_number returned no ETag" >&2; abort_upload; exit 1; }

  echo "    part $part_number/$PARTS uploaded"
  ETAGS+=("\"$etag\"")
  PNUMS+=("$part_number")
done

parts_json="["
for idx in "${!ETAGS[@]}"; do
  [ "$idx" -gt 0 ] && parts_json+=","
  parts_json+="{\"ETag\":${ETAGS[$idx]},\"PartNumber\":${PNUMS[$idx]}}"
done
parts_json+="]"
echo "==> finish upload"
FINISH="$(api_post "$BASE/api/experimental/usermedia/$UUID/finish-upload/" "{\"parts\":$parts_json}")"
echo "    finish-upload: $(jq -r '.uuid // "ok"' <<<"$FINISH")"

categories_json='[]'
if [ -n "$CATEGORIES" ]; then
  categories_json="$(jq -n -c --arg c "$CATEGORIES" '$c | split(",") | map(gsub("^[[:space:]]+|[[:space:]]+$"; ""))')"
fi
SUBMIT_BODY="$(jq -n \
  --arg author "$NAMESPACE" \
  --argjson categories "$categories_json" \
  --arg community "$COMMUNITY" \
  --argjson nsfw "$NSFW_JSON" \
  --arg uuid "$UUID" \
  '{author_name:$author,categories:$categories,communities:[$community],has_nsfw_content:$nsfw,upload_uuid:$uuid}')"
echo "==> submit"
SUBMIT="$(api_post "$BASE/api/experimental/submission/submit/" "$SUBMIT_BODY")"

full_name="$(jq -r '.package_version.full_name // empty' <<<"$SUBMIT")"
version="$(jq -r '.package_version.version_number // empty' <<<"$SUBMIT")"
[ -n "$full_name" ] || { echo "submit returned no package_version.full_name: $SUBMIT" >&2; exit 1; }
[ -n "$version" ] || { echo "submit returned no package_version.version_number: $SUBMIT" >&2; exit 1; }

echo "published: $full_name $version"
if [ -n "${GITHUB_OUTPUT:-}" ]; then
  {
    echo "package=$full_name"
    echo "version=$version"
  } >> "$GITHUB_OUTPUT"
fi
