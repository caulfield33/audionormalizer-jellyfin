# Debugging

From fastest to deepest. In 90% of cases the first step is enough.

---

## Level 0. The Diagnostics button

Every film in the report has a **Diagnostics** button. It changes nothing on disk: it runs
only the measurement pass and shows everything the plugin can see for that film.

The same over the API:

```bash
curl -s 'http://localhost:8096/AudioNormalizer/SelfTest/<itemId>?run=true' \
  -H 'Authorization: MediaBrowser Token="YOUR_TOKEN"' | jq
```

What you get:

```
Film:             Dune (2021)
File:             /media/films/Dune (2021)/Dune (2021) [2160p].mkv

Audio tracks:
  [x] index 1: EAC3 / 5.1 / ukr [default]
  [ ] index 2: AC3 / stereo / eng
  [ ] index 3: AAC / stereo / eng / Director's Commentary [commentary]
Selected:         index 1: EAC3 / 5.1 / ukr

Output path:      /media/films/Dune (2021)/Dune (2021) [2160p].ukr.AN - Ukrainian 5-1.mka
Folder writable:  yes
Free space:       412.7 GB
ffmpeg:           /usr/lib/jellyfin-ffmpeg/ffmpeg
Strength (m):     14.8

Measurements:
  index 1: -14.7 LUFS, range 25.4 LU (quiet -40.6 / loud -15.2), peak -5.5 dBTP
  index 2: -20.2 LUFS, range 26.2 LU (quiet -46.9 / loud -20.7), peak -5.6 dBTP

--- analysis command (paste into a terminal) ---
ffmpeg -hide_banner -nostdin ... (the full command)

--- encode command ---
ffmpeg -hide_banner ... (the full command)

--- ffmpeg output ---
(last 40 lines)
```

That answers most questions in one go: whether the file was found at all, **which tracks are
selected** (and whether a commentary track slipped in), where the result will land, whether
the folder is writable, and what was actually measured.

The commands there can be **pasted into a terminal verbatim** — the quoting is already
correct, including paths with spaces, brackets and Cyrillic. This has been verified.

---

## Level 1. Server logs

**Where:** Dashboard → Logs, or the file `<config>/log/log_YYYYMMDD.log`.
Every message from the plugin starts with `Audio Normalizer:`.

```bash
grep "Audio Normalizer" /config/log/log_*.log | tail -50
```

**To turn on verbose logging**, create or edit `<config>/logging.json`:

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "System": "Warning",
        "Jellyfin.Plugin.AudioNormalizer": "Debug"
      }
    }
  }
}
```

**No restart needed** — Jellyfin re-reads this file live. At `Debug` level the full ffmpeg
command for every job goes into the log.

If you would rather not touch JSON, the plugin settings have a
**Log the full ffmpeg command** checkbox that does the same at Information level.

---

## Level 2. Reproduce it by hand

Take the command from Diagnostics and run it in a terminal on the server (inside the
container, for Docker):

```bash
docker exec -it jellyfin bash
# paste the command
```

This separates "the plugin is doing something wrong" from "ffmpeg cannot handle this".
If the command fails in a terminal, the problem is in ffmpeg or in the file, not the plugin.

Typical ffmpeg responses and what they mean:

| Message | Cause |
|---|---|
| `No such filter: 'dynaudnorm'` | an ffmpeg build without that filter → switch the engine to Compressor |
| `Unable to choose an output format` | the output extension is unknown — this should be handled via `-f`; if you see it, report it |
| `Invalid channel layout` | an unusual channel layout → try Plain stereo |
| `Permission denied` | the folder is read-only |
| `Output file is empty` | the selected track is empty or damaged → pick a different source |

---

## Level 3. Plugin state

The plugin remembers what it did to what, in:

```
<config>/plugins/Audio Normalizer/state.json
```

Per film: source size and modification time, every audio track with its measurements,
selection, output path, settings fingerprint and status.

```bash
jq '.Records[] | select(.AudioTracks[]?.State=="Failed") | {ItemName, tracks:[.AudioTracks[]|{StreamIndex,State,LastError}]}' state.json
jq '.Records[] | {ItemName, ranges:[.AudioTracks[]|.Source.LoudnessRangeLu]}' state.json | head -40
```

**Reset everything and start over:** stop Jellyfin, delete `state.json`, start it again.
Generated tracks stay on disk; the plugin simply re-measures them.

**Reset one film:** the Delete button in the report, then Build.

---

## Symptom → where to look

**The plugin does not appear in the plugin list.**
That is not plugin debugging, that is load debugging. The reason will be in the startup log —
look for `Failed to load assembly` or `targetAbi`. The usual cause: built for the wrong
Jellyfin version. `meta.json` carries `targetAbi` 12.0.0.0 or 10.11.0.0 and it must match the
server. The layout must be exactly:

```
<config>/plugins/Audio Normalizer/Jellyfin.Plugin.AudioNormalizer.dll
<config>/plugins/Audio Normalizer/meta.json
```

with no extra nested folder.

**The track was created but the player doesn't show it.**
The most common problem. In order:

1. Check that the file physically sits **in the same folder** as the video and its name starts
   exactly with the video's file name plus a dot:
   `Film.mkv` → `Film.ukr.AN - Ukrainian 5-1.mka`. One extra character and Jellyfin ignores it.
2. Refresh the item: the three dots → Refresh metadata → **Scan for new and updated files**.
3. During the refresh the log should contain `Refreshing ... due to external audio change`.
   If it does not, the server never saw the file — back to step 1.
4. Check the extension is in Jellyfin's audio list: `.mka`, `.m4a`, `.ac3`, `.eac3`, `.flac`,
   `.opus` all qualify.

**The track is there but has no name, or is marked forced.**
The name contains a reserved word. Jellyfin parses names by dots and matches `default`,
`forced` and `foreign` **as substrings**, `cc`, `hi` and `sdh` **exactly**. The plugin
sanitizes this, but check your prefix if you changed it.

**Audio drifts out of sync with the video.**
First find out by how much: Diagnostics shows the codec. AAC has 21 ms of delay, E-AC3 5 ms,
FLAC none. The delay is declared in the container and a compliant player compensates for it.
If yours does not, either switch to FLAC or set a manual delay with the opposite sign.

**The wrong track was picked.**
Click the film's name in the report — it expands into a list of all its audio tracks with
checkboxes. Tick the ones you want normalized; the choice saves immediately. In Diagnostics
the selected tracks are marked `[x]`. The global automatic rule is "Which track to use as the
source" in the settings, along with the commentary keyword list.

**The queue is stuck.**
`GET /AudioNormalizer/Status`. If `PausedForPlayback: true`, somebody is watching — that is
deliberate. If `Pending > 0` and `Running` stays empty for a long time, check the log for
ffmpeg startup errors.

**Everything is being skipped.**
Either the range is below the threshold (which is correct and intended), or the folder is not
writable, or no track is selected. Diagnostics says which of those it is.

**Too flat, or a pumping effect.**
Raise "Allowed range" to 12–14 LU, or switch the engine to Compressor. Pumping usually means
the strength is too high for that material.

---

## Narrowing it down quickly: plugin or audio?

If you are unsure where the problem lies, run the standalone script on one film:

```bash
./tools/normalize-library.sh --dry-run "/media/films/That Film"
```

It does exactly the same work without Jellyfin. If the script succeeds and the plugin does
not, the problem is in the integration. If both fail, the problem is in ffmpeg or in the file.

---

## What to send if you need help

1. The full Diagnostics output for the problem film.
2. `grep "Audio Normalizer" /config/log/log_*.log | tail -50`
3. Your Jellyfin version and `ffmpeg -version` from inside the container.
4. `ffprobe -v error -show_streams "film.mkv" | grep -A5 codec_type=audio`
