#!/usr/bin/env bash
#
# Builds the plugin and packages it the way Jellyfin expects: a zip holding the
# assembly plus a meta.json.
#
#   ./build.sh            # build for Jellyfin 12.x  (net10.0)
#   ./build.sh 10.11      # build for Jellyfin 10.11.x (net9.0)
#   ./build.sh both       # build both
#
# Needs the matching .NET SDK and access to nuget.org for the Jellyfin packages.
#
# Version scheme (see the csproj for the reasoning): the 10.11 build is 1.0.0.x and
# the 12 build is 2.0.0.x, so a single repository manifest can serve both servers.
# Set RELEASE=n to bump the last component: RELEASE=3 ./build.sh both

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$ROOT/src/Jellyfin.Plugin.AudioNormalizer/Jellyfin.Plugin.AudioNormalizer.csproj"
ARTIFACTS="$ROOT/artifacts"
PLUGIN_NAME="Audio Normalizer"
PLUGIN_GUID="2a968ad7-6168-44c8-b149-cf94eb870b25"
RELEASE="${RELEASE:-0}"

build_one() {
    local jfver="$1" tfm abi outdir stage version

    # abi must stay equal to JellyfinPackageVersion in the csproj. The server only checks
    # that its own version is >= targetAbi; it never checks which API the DLL was compiled
    # against, so a targetAbi lower than the referenced package is a promise the binary
    # cannot keep and the plugin loads as NotSupported.
    case "$jfver" in
        12)    tfm="net10.0"; abi="12.0.0.0";  version="2.0.0.$RELEASE" ;;
        10.11) tfm="net9.0";  abi="10.11.0.0"; version="1.0.0.$RELEASE" ;;
        *) echo "Unknown Jellyfin version '$jfver'. Use 12 or 10.11." >&2; return 1 ;;
    esac

    echo "==> Building for Jellyfin $jfver ($tfm), plugin version $version"
    outdir="$ROOT/build/out/$jfver"
    rm -rf "$outdir"

    dotnet publish "$PROJECT" \
        -c Release \
        -p:JellyfinVersion="$jfver" \
        -p:PluginVersion="$version" \
        -o "$outdir" \
        --nologo

    stage="$ROOT/build/stage/$jfver"
    rm -rf "$stage"
    mkdir -p "$stage"

    # Only the plugin assembly belongs in the zip. Anything the server already ships
    # would shadow the real one at load time and break things.
    cp "$outdir/Jellyfin.Plugin.AudioNormalizer.dll" "$stage/"
    [ -f "$outdir/Jellyfin.Plugin.AudioNormalizer.pdb" ] && cp "$outdir/Jellyfin.Plugin.AudioNormalizer.pdb" "$stage/" || true

    cat > "$stage/meta.json" <<EOF
{
    "category": "General",
    "guid": "$PLUGIN_GUID",
    "name": "$PLUGIN_NAME",
    "description": "Builds a second, loudness-normalized audio track next to each film so dialogue stays audible without explosions being painful. Originals are never modified.",
    "overview": "Normalized companion audio tracks",
    "owner": "self-hosted",
    "targetAbi": "$abi",
    "framework": "$tfm",
    "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
    "version": "$version",
    "changelog": "Initial release."
}
EOF

    mkdir -p "$ARTIFACTS"
    local zip="$ARTIFACTS/audio-normalizer_${version}.zip"
    rm -f "$zip"
    (cd "$stage" && zip -q -r "$zip" .)

    # Recorded next to the zip so make-manifest.sh does not have to re-derive any of it.
    cat > "$ARTIFACTS/audio-normalizer_${version}.meta" <<EOF
version=$version
targetAbi=$abi
jellyfin=$jfver
zip=$(basename "$zip")
checksum=$(md5sum "$zip" | cut -d' ' -f1)
timestamp=$(date -u +%Y-%m-%dT%H:%M:%SZ)
EOF

    echo "    -> $zip"
}

case "${1:-12}" in
    both) build_one 12; build_one 10.11 ;;
    *)    build_one "${1:-12}" ;;
esac

echo
echo "Install by hand: extract the zip into your Jellyfin config folder under"
echo "  plugins/Audio Normalizer/"
echo "then restart the server."
echo
echo "Or publish a repository: ./tools/make-manifest.sh <base-url>"
