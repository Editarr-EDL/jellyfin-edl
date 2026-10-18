#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MEDIA_ROOT="$ROOT_DIR/dev/media"

VALID_DIR="$MEDIA_ROOT/valid"
MISSING_DIR="$MEDIA_ROOT/missing"
INVALID_DIR="$MEDIA_ROOT/invalid"

mkdir -p "$VALID_DIR" "$MISSING_DIR" "$INVALID_DIR"

# Placeholder files are enough for path/EDL sidecar validation during API flow checks.
# Replace these with real media files later if you want playback-level validation.
touch "$VALID_DIR/valid.mkv"
touch "$MISSING_DIR/missing.mkv"
touch "$INVALID_DIR/invalid.mkv"

cat > "$VALID_DIR/valid.edl" <<'EOF'
10.000 20.000 cut
30 35 mute
EOF

cat > "$INVALID_DIR/invalid.edl" <<'EOF'
abc 20 cut
10 5 mute
EOF

cat > "$MEDIA_ROOT/README.txt" <<'EOF'
Fixture folders:
- valid/valid.mkv + valid.edl (expected API status 200)
- missing/missing.mkv with no sidecar (expected API status 404)
- invalid/invalid.mkv + invalid.edl (expected API status 422)

Notes:
1. In Jellyfin admin, add /media as a library path in the running dev container.
2. Run a library scan before resolving item IDs.
3. Replace placeholder .mkv files with real media if your environment requires probeable media files for indexing.
EOF

echo "Created fixture structure under $MEDIA_ROOT"
