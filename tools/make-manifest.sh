#!/usr/bin/env bash
#
# make-manifest.sh - builds the repository manifest Jellyfin reads when you add a
# plugin repository URL.
#
#   ./build.sh both
#   ./tools/make-manifest.sh https://github.com/USER/REPO/releases/download/v1 \
#        https://raw.githubusercontent.com/USER/REPO/main/manifest.json
#
# The base URL is where the zips will actually be reachable. For a GitHub release that
# is  https://github.com/USER/REPO/releases/download/<tag>  and the file name is appended.
#
# Then point Jellyfin at the manifest itself, for example
#   https://raw.githubusercontent.com/USER/REPO/main/manifest.json
#
# How the server uses it: it keeps only the entries whose targetAbi is <= its own
# version, then installs the highest plugin version left. That is why the 12 build
# carries 2.0.0.x and the 10.11 build 1.0.0.x - a 10.11 server never sees the 12 entry,
# and a 12 server prefers it. Both lines can live in one manifest.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARTIFACTS="$ROOT/artifacts"
OUT="$ROOT/manifest.json"

PLUGIN_GUID="2a968ad7-6168-44c8-b149-cf94eb870b25"
PLUGIN_NAME="Audio Normalizer"
REPO_NAME="${REPO_NAME:-Audio Normalizer}"
CHANGELOG="${CHANGELOG:-}"

BASE_URL="${1:-}"
[ -n "$BASE_URL" ] || { sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; exit 1; }
BASE_URL="${BASE_URL%/}"

shopt -s nullglob
metas=("$ARTIFACTS"/*.meta)
[ ${#metas[@]} -gt 0 ] || { echo "No builds in $ARTIFACTS. Run ./build.sh both first." >&2; exit 1; }

# Where the manifest itself will be served from. This is the URL pasted into Jellyfin,
# and it is usually NOT the same place as the zips (raw.githubusercontent vs releases).
MANIFEST_URL="${2:-${MANIFEST_URL:-}}"
if [ -z "$MANIFEST_URL" ]; then
    MANIFEST_URL="$BASE_URL/manifest.json"
    echo "note: no manifest URL given, recording $MANIFEST_URL" >&2
    echo "      pass it as the second argument if the manifest lives elsewhere," >&2
    echo "      e.g. https://raw.githubusercontent.com/USER/REPO/main/manifest.json" >&2
fi

entries=""
for m in "${metas[@]}"; do
    # shellcheck disable=SC1090
    version=""; targetAbi=""; zip=""; checksum=""; timestamp=""
    while IFS='=' read -r k v; do
        case "$k" in
            version) version="$v" ;;
            targetAbi) targetAbi="$v" ;;
            zip) zip="$v" ;;
            checksum) checksum="$v" ;;
            timestamp) timestamp="$v" ;;
        esac
    done < "$m"

    [ -n "$version" ] || continue

    # Re-verify against the file on disk: a stale .meta next to a rebuilt zip would
    # publish a checksum the server then rejects, which is a confusing failure.
    if [ -f "$ARTIFACTS/$zip" ]; then
        actual=$(md5sum "$ARTIFACTS/$zip" | cut -d' ' -f1)
        if [ "$actual" != "$checksum" ]; then
            echo "note: checksum for $zip changed since build, using the current one" >&2
            checksum="$actual"
        fi
    else
        echo "warning: $zip is missing from $ARTIFACTS, skipping" >&2
        continue
    fi

    [ -n "$entries" ] && entries="$entries,"
    entries="$entries
        {
            \"version\": \"$version\",
            \"changelog\": $(printf '%s' "$CHANGELOG" | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read()))'),
            \"targetAbi\": \"$targetAbi\",
            \"sourceUrl\": \"$BASE_URL/$zip\",
            \"checksum\": \"$checksum\",
            \"timestamp\": \"$timestamp\",
            \"repositoryName\": \"$REPO_NAME\",
            \"repositoryUrl\": \"$MANIFEST_URL\"
        }"
done

cat > "$OUT" <<EOF
[
    {
        "guid": "$PLUGIN_GUID",
        "name": "$PLUGIN_NAME",
        "description": "Builds a second, loudness-normalized audio track next to each film so dialogue stays audible without explosions being painful. Originals are never modified.",
        "overview": "Normalized companion audio tracks",
        "owner": "self-hosted",
        "category": "General",
        "imageUrl": null,
        "versions": [$entries
        ]
    }
]
EOF

python3 -c "import json,sys; json.load(open('$OUT')); print('manifest.json is valid JSON')"
echo "Wrote $OUT"
echo
echo "Versions published:"
python3 - "$OUT" <<'PY'
import json, sys
for pkg in json.load(open(sys.argv[1])):
    for v in pkg["versions"]:
        print(f"  {v['version']:<12} targetAbi {v['targetAbi']:<12} {v['sourceUrl']}")
PY
