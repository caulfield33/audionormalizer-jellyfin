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
# Version scheme (see the csproj for the reasoning). RELEASE carries your own version, one to
# three numbers, and the build prepends the Jellyfin line:
#
#   RELEASE=0.0.5 ./build.sh both   ->  10.11 build 1.0.0.5,  12 build 2.0.0.5
#
# The leading 1 or 2 is not part of your version - it is what keeps the 12 build sorting above
# the 10.11 build in a shared manifest, which is the whole reason one manifest can serve both.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$ROOT/src/Jellyfin.Plugin.AudioNormalizer/Jellyfin.Plugin.AudioNormalizer.csproj"
ARTIFACTS="$ROOT/artifacts"
PLUGIN_NAME="Audio Normalizer"
PLUGIN_GUID="2a968ad7-6168-44c8-b149-cf94eb870b25"
PLUGIN_OWNER="${PLUGIN_OWNER:-Vasyl Lukinchuk}"

# Shown in the plugin catalogue. The release workflow passes real notes; the fallback points
# at the GitHub release rather than lying with a fixed "Initial release." It goes straight
# into meta.json, so keep it free of quotes and newlines.
CHANGELOG="${CHANGELOG:-See the release notes for this version.}"

RELEASE="${RELEASE:-0.0.0}"

# Validated here rather than in the workflow: this is where the version string is assembled,
# so a hand-run build is checked too. One to three numbers, because the Jellyfin line prefix
# takes the first slot and .NET rejects a five-component assembly version. Each part must also
# fit in 16 bits, which is the AssemblyVersion limit.
if ! [[ "$RELEASE" =~ ^[0-9]+(\.[0-9]+){0,2}$ ]]; then
    echo "RELEASE must be one to three numbers, for example 0.0.5 or 1.2.0 - got '$RELEASE'." >&2
    exit 1
fi

build_one() {
    local jfver="$1" tfm abi outdir stage version

    # abi must stay equal to JellyfinPackageVersion in the csproj. The server only checks
    # that its own version is >= targetAbi; it never checks which API the DLL was compiled
    # against, so a targetAbi lower than the referenced package is a promise the binary
    # cannot keep and the plugin loads as NotSupported.
    case "$jfver" in
        12)    tfm="net10.0"; abi="12.0.0.0";  version="2.$RELEASE" ;;
        10.11) tfm="net9.0";  abi="10.11.0.0"; version="1.$RELEASE" ;;
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

    # MIT asks for the notice to travel with every copy, and a plain text file next to
    # meta.json is inert at load time - the rule above is about assemblies, not documents.
    cp "$ROOT/LICENSE" "$stage/"

    # Jellyfin serves this as the plugin icon via meta.json's imagePath.
    cp "$ROOT/icon.svg" "$stage/"

    cat > "$stage/meta.json" <<EOF
{
    "category": "General",
    "guid": "$PLUGIN_GUID",
    "name": "$PLUGIN_NAME",
    "description": "Builds a second, loudness-normalized audio track next to each film so dialogue stays audible without explosions being painful. Originals are never modified.",
    "overview": "Normalized companion audio tracks",
    "imagePath": "icon.svg",
    "owner": "$PLUGIN_OWNER",
    "targetAbi": "$abi",
    "framework": "$tfm",
    "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
    "version": "$version",
    "changelog": "$CHANGELOG"
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
