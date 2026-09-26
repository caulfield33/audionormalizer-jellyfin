#!/usr/bin/env bash
#
# normalize-library.sh - the same pipeline the plugin uses, as a plain script.
#
# Walks a folder of films, measures each one, and writes a normalized companion
# audio track next to it using the file naming Jellyfin needs to pick it up as an
# extra audio track. Originals are never touched.
#
#   ./normalize-library.sh /media/films
#   ./normalize-library.sh --dry-run /media/films
#   ./normalize-library.sh --target -16 --range 9 --peak -1.5 /media/films
#
# Requires ffmpeg and ffprobe.

set -uo pipefail

TARGET_LUFS=-16
TARGET_RANGE=9
MAX_PEAK=-1.5
SKIP_BELOW=7
CODEC=aac
BITRATE=256k
CONTAINER=mka
TITLE="Normalized"
CENTER_DB=3
SURROUND_DB=-3
LFE_DB=-60
DRY_RUN=0
FORCE=0
ROOT=""

die() { echo "error: $*" >&2; exit 1; }

usage() {
    sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'
    exit 0
}

while [ $# -gt 0 ]; do
    case "$1" in
        --target)  TARGET_LUFS="$2"; shift 2 ;;
        --range)   TARGET_RANGE="$2"; shift 2 ;;
        --peak)    MAX_PEAK="$2"; shift 2 ;;
        --skip-below) SKIP_BELOW="$2"; shift 2 ;;
        --codec)   CODEC="$2"; shift 2 ;;
        --bitrate) BITRATE="$2"; shift 2 ;;
        --title)   TITLE="$2"; shift 2 ;;
        --dry-run) DRY_RUN=1; shift ;;
        --force)   FORCE=1; shift ;;
        -h|--help) usage ;;
        -*) die "unknown option $1" ;;
        *) ROOT="$1"; shift ;;
    esac
done

[ -n "$ROOT" ] || usage
[ -d "$ROOT" ] || die "not a folder: $ROOT"
command -v ffmpeg >/dev/null || die "ffmpeg not found"
command -v ffprobe >/dev/null || die "ffprobe not found"

# --- helpers ---------------------------------------------------------------

# Clip-safe dialogue-forward downmix. The coefficients are scaled by their own sum so
# they can never add up past unity; an unscaled matrix clips on loud scenes.
build_pan() {
    local channels="$1"
    awk -v c="$CENTER_DB" -v s="$SURROUND_DB" -v l="$LFE_DB" -v ch="$channels" 'BEGIN{
        front = 1.0
        center = (c <= -59.9) ? 0 : 10 ^ (c/20)
        surr   = (s <= -59.9) ? 0 : 10 ^ (s/20)
        lfe    = (l <= -59.9) ? 0 : 10 ^ (l/20)
        if (ch < 6) lfe = 0
        if (ch < 5) surr = 0
        sum = front + center + surr + lfe
        if (sum <= 0) { print "pan=stereo|FL=FL|FR=FR"; exit }
        f = front/sum; cc = center/sum; ss = surr/sum; ll = lfe/sum
        bl = (ch >= 7) ? "SL" : "BL"
        br = (ch >= 7) ? "SR" : "BR"
        left  = sprintf("%.4f*FL", f)
        right = sprintf("%.4f*FR", f)
        if (cc > 0)              { left = left sprintf("+%.4f*FC", cc); right = right sprintf("+%.4f*FC", cc) }
        if (ss > 0 && ch >= 5)   { left = left sprintf("+%.4f*%s", ss, bl); right = right sprintf("+%.4f*%s", ss, br) }
        if (ll > 0.0005 && ch>=6){ left = left sprintf("+%.4f*LFE", ll); right = right sprintf("+%.4f*LFE", ll) }
        printf "pan=stereo|FL=%s|FR=%s\n", left, right
    }'
}

# Maps how much loudness range has to disappear onto dynaudnorm's max gain factor.
pick_max_gain() {
    awk -v have="$1" -v want="$TARGET_RANGE" 'BEGIN{
        need = have - want
        if (need <= 2) { print 2; exit }
        split("2 4 8 12 15 17", r, " ")
        split("2 4 6 8 12 16", g, " ")
        for (i = 2; i <= 6; i++) {
            if (need <= r[i]) {
                span = r[i] - r[i-1]
                f = (span <= 0) ? 0 : (need - r[i-1]) / span
                printf "%.1f\n", g[i-1] + f * (g[i] - g[i-1]); exit
            }
        }
        print 16
    }'
}

json_num() { sed -n "s/.*\"$2\"[^\"]*\"\\([^\"]*\\)\".*/\\1/p" <<< "$1" | head -1; }

sanitize_title() {
    # Jellyfin reads these words as flags, and a two or three letter word as a language.
    local t="${1//./-}"
    case "${t,,}" in
        default|forced|foreign|cc|hi|sdh) t="${t}-track" ;;
    esac
    if [[ ${#t} -le 3 && "$t" =~ ^[A-Za-z]+$ ]]; then t="${t}-track"; fi
    echo "$t"
}

# --- main ------------------------------------------------------------------

SAFE_TITLE="$(sanitize_title "$TITLE")"
# Peak headroom: alimiter limits sample peaks, the target is a true peak.
LIMIT=$(awk -v p="$MAX_PEAK" 'BEGIN{ printf "%.5f", 10 ^ ((p - 0.3)/20) }')

# The temp file ends in .an-part on purpose, so a half-written track is never scanned.
# ffmpeg cannot infer a muxer from that extension, so name it explicitly.
case "$CONTAINER" in
    mka) MUXER=matroska ;;
    m4a) MUXER=ipod ;;
    ac3) MUXER=ac3 ;;
    eac3) MUXER=eac3 ;;
    flac) MUXER=flac ;;
    opus) MUXER=opus ;;
    *)   MUXER=matroska ;;
esac

total=0; made=0; skipped=0; failed=0

while IFS= read -r -d '' file; do
    total=$((total+1))
    base="${file%.*}"
    dir="$(dirname "$file")"

    # Pick the audio track with the most channels, ignoring anything already external.
    read -r idx channels lang <<< "$(ffprobe -v error -select_streams a \
        -show_entries stream=index,channels:stream_tags=language \
        -of csv=p=0 "$file" 2>/dev/null | awk -F, 'NF>=2 {if ($2+0 > best) {best=$2+0; line=$1" "$2" "($3==""?"und":$3)}} END{print line}')"

    if [ -z "${idx:-}" ]; then
        echo "SKIP  $(basename "$file") - no audio track"
        skipped=$((skipped+1)); continue
    fi

    [ "$lang" = "und" ] && langpart="" || langpart=".$lang"
    out="${base}${langpart}.${SAFE_TITLE}.${CONTAINER}"
    tmp="${out}.an-part"

    if [ -f "$out" ] && [ "$FORCE" -eq 0 ]; then
        echo "HAVE  $(basename "$file")"
        skipped=$((skipped+1)); continue
    fi

    pan="$(build_pan "$channels")"
    [ "$channels" -le 2 ] && pan=""

    # One decode, two measurements: the source numbers for the report and the
    # loudnorm numbers for the encode.
    pre="${pan:+$pan,}"
    graph="[0:${idx}]asplit=2[src][proc];[src]ebur128=peak=true:framelog=quiet[m];[proc]${pre}loudnorm=I=${TARGET_LUFS}:TP=${MAX_PEAK}:LRA=20:print_format=json[a]"

    analysis=$(ffmpeg -hide_banner -nostdin -nostats -loglevel info -i "$file" \
        -filter_complex "$graph" -map "[m]" -map "[a]" -f null - 2>&1)

    src_lra=$(sed -n 's/^ *LRA: *\(-\?[0-9.]*\) LU/\1/p' <<< "$analysis" | head -1)
    src_i=$(sed -n 's/^ *I: *\(-\?[0-9.]*\) LUFS/\1/p' <<< "$analysis" | head -1)

    if [ -z "$src_lra" ]; then
        echo "FAIL  $(basename "$file") - could not measure"
        failed=$((failed+1)); continue
    fi

    if awk -v a="$src_lra" -v b="$SKIP_BELOW" 'BEGIN{exit !(a < b)}'; then
        printf "FLAT  %s - range %s LU already below %s\n" "$(basename "$file")" "$src_lra" "$SKIP_BELOW"
        skipped=$((skipped+1)); continue
    fi

    mi=$(json_num "$analysis" input_i)
    mtp=$(json_num "$analysis" input_tp)
    mlra=$(json_num "$analysis" input_lra)
    mth=$(json_num "$analysis" input_thresh)
    moff=$(json_num "$analysis" target_offset)

    gain=$(pick_max_gain "$src_lra")
    dyn="dynaudnorm=f=220:g=21:p=0.85:m=${gain}:s=11:b=1"

    if [ -n "$mi" ] && [ "$mi" != "-inf" ]; then
        ln="loudnorm=I=${TARGET_LUFS}:TP=${MAX_PEAK}:LRA=20:linear=true:measured_I=${mi}:measured_TP=${mtp}:measured_LRA=${mlra}:measured_thresh=${mth}:offset=${moff}"
    else
        ln="loudnorm=I=${TARGET_LUFS}:TP=${MAX_PEAK}:LRA=20"
    fi

    chain="${pre}${dyn},${ln},alimiter=limit=${LIMIT}:level=false:attack=5:release=50,aresample=48000:resampler=soxr"

    printf "%-5s %s  (%s LUFS, range %s LU, m=%s)\n" \
        "$([ "$DRY_RUN" -eq 1 ] && echo DRY || echo MAKE)" "$(basename "$file")" "$src_i" "$src_lra" "$gain"

    [ "$DRY_RUN" -eq 1 ] && continue

    # -ar is not optional: without it loudnorm resamples to 192 kHz and the file
    # comes out four times bigger.
    if ffmpeg -hide_banner -nostdin -nostats -loglevel error -y -i "$file" \
        -map "0:${idx}" -vn -sn -dn -map_chapters -1 \
        -af "$chain" -ar 48000 -ac 2 \
        -c:a "$CODEC" -b:a "$BITRATE" \
        ${lang:+-metadata:s:a:0 "language=$lang"} \
        -metadata:s:a:0 "title=$SAFE_TITLE" \
        -disposition:a:0 0 \
        -f "$MUXER" \
        "$tmp"
    then
        mv -f "$tmp" "$out"
        made=$((made+1))
        # -nostdin matters here: this loop's stdin is the file list, and an ffmpeg
        # without it will read and swallow that list, hanging the whole run.
        result=$(ffmpeg -hide_banner -nostdin -nostats -i "$out" -map 0:a:0 \
            -af ebur128=peak=true:framelog=quiet -f null - 2>&1 \
            | sed -n 's/^ *LRA: *\(-\?[0-9.]*\) LU/\1/p' | head -1)
        printf "      -> %s LU (was %s)\n" "$result" "$src_lra"
    else
        rm -f "$tmp"
        echo "      -> FAILED"
        failed=$((failed+1))
    fi

done < <(find "$ROOT" -type f \( -iname '*.mkv' -o -iname '*.mp4' -o -iname '*.avi' -o -iname '*.m4v' -o -iname '*.ts' \) -print0)

echo
echo "scanned $total, made $made, skipped $skipped, failed $failed"
[ "$made" -gt 0 ] && echo "Run a library scan in Jellyfin so the new tracks show up."
exit 0
