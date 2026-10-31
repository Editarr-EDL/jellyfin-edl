#!/usr/bin/env bash
# Usage:
#   run-edl-matrix.sh EVIDENCE_DIR CASES_JSON LABEL
#     Per-user EDL and MCF cases against a verify.py launch; see the verify-jellyfin-edl skill.
#   run-edl-matrix.sh
#     The original manual 200/404/422 EDL smoke for one token, configured by the variables below.
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [ "$#" -gt 0 ]; then
  exec python3 -B "$ROOT_DIR/.github/skills/verify-jellyfin-edl/scripts/verify.py" matrix "$@"
fi

: "${JELLYFIN_USER_TOKEN:?Set JELLYFIN_USER_TOKEN to a signed-in user access token}"
: "${JELLYFIN_ITEM_ID_VALID:?Set JELLYFIN_ITEM_ID_VALID}"
: "${JELLYFIN_ITEM_ID_MISSING_EDL:?Set JELLYFIN_ITEM_ID_MISSING_EDL}"
: "${JELLYFIN_ITEM_ID_INVALID_EDL:?Set JELLYFIN_ITEM_ID_INVALID_EDL}"

run_case() {
  local item_id="$1"
  local expected="$2"
  JELLYFIN_USER_TOKEN="$JELLYFIN_USER_TOKEN" \
  JELLYFIN_ITEM_ID="$item_id" \
  EXPECTED_STATUS="$expected" \
  "$ROOT_DIR/scripts/jellyfin-smoke.sh"
}

echo "Running 200 case (valid EDL)"
run_case "$JELLYFIN_ITEM_ID_VALID" 200

echo "Running 404 case (missing EDL)"
run_case "$JELLYFIN_ITEM_ID_MISSING_EDL" 404

echo "Running 422 case (invalid EDL)"
run_case "$JELLYFIN_ITEM_ID_INVALID_EDL" 422

echo "Matrix complete"
