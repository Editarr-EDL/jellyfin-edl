#!/usr/bin/env bash
set -euo pipefail

BASE_URL="${JELLYFIN_BASE_URL:-http://localhost:8096}"
USER_TOKEN="${JELLYFIN_USER_TOKEN:-}"
ITEM_ID="${JELLYFIN_ITEM_ID:-}"
EXPECTED_STATUS="${EXPECTED_STATUS:-200}"

log() {
  printf '[smoke] %s\n' "$1"
}

log "Waiting for Jellyfin at $BASE_URL"
for _ in {1..60}; do
  if curl -fsS "$BASE_URL/System/Info/Public" >/dev/null 2>&1; then
    log "Jellyfin is responding"
    break
  fi
  sleep 2
done

if ! curl -fsS "$BASE_URL/System/Info/Public" >/dev/null 2>&1; then
  log "Jellyfin did not become ready in time"
  exit 1
fi

log "Checking container logs for plugin assembly mentions"
if docker compose logs jellyfin 2>/dev/null | grep -i "Jellyfin.Plugin.Edl" >/dev/null; then
  log "Found Jellyfin.Plugin.Edl in server logs"
else
  log "Did not find Jellyfin.Plugin.Edl in logs yet. This can happen before first full startup scan."
fi

PROBE_STATUS="$(curl -sS -o /dev/null -w "%{http_code}" "$BASE_URL/Plugins/Edl/ByItem/00000000-0000-0000-0000-000000000000")"
if [[ "$PROBE_STATUS" == "401" || "$PROBE_STATUS" == "403" ]]; then
  log "Endpoint route is reachable and auth is enforced (HTTP $PROBE_STATUS)"
else
  log "Unexpected unauthenticated probe status: HTTP $PROBE_STATUS"
  exit 1
fi

if [[ -z "$USER_TOKEN" || -z "$ITEM_ID" ]]; then
  log "Skipping authenticated endpoint call. Set JELLYFIN_USER_TOKEN and JELLYFIN_ITEM_ID to enable full check."
  exit 0
fi

TMP_HEADERS="$(mktemp)"
TMP_BODY="$(mktemp)"
trap 'rm -f "$TMP_HEADERS" "$TMP_BODY"' EXIT

STATUS="$(curl -sS -o "$TMP_BODY" -D "$TMP_HEADERS" \
  -H "X-Emby-Token: $USER_TOKEN" \
  "$BASE_URL/Plugins/Edl/ByItem/$ITEM_ID" \
  -w "%{http_code}")"

log "Endpoint returned HTTP $STATUS"
if [[ "$STATUS" != "$EXPECTED_STATUS" ]]; then
  log "Expected HTTP $EXPECTED_STATUS"
  log "Response headers:"
  cat "$TMP_HEADERS"
  log "Response body:"
  cat "$TMP_BODY"
  exit 1
fi

if [[ "$STATUS" == "200" ]]; then
  CONTENT_TYPE="$(grep -i '^content-type:' "$TMP_HEADERS" | tr -d '\r' | awk '{print $2}')"
  if [[ "$CONTENT_TYPE" != text/plain* ]]; then
    log "Unexpected content type for success: $CONTENT_TYPE"
    exit 1
  fi
fi

log "Smoke test passed"
