# Audio Normalizer for Jellyfin

Builds a **second audio track** next to each film: dialogue you can hear, explosions that
don't knock you off the sofa. The original file is never modified — the player simply gains
another audio option, and you can switch back whenever you like.

On the test material (an action-film mock-up: quiet centre dialogue, loud LFE explosions):

| Measurement | Source | After |
|---|---:|---:|
| Integrated loudness | −14.7 LUFS | −16.8 LUFS |
| **Quiet-to-loud range (LRA)** | **25.4 LU** | **10.0 LU** |
| Dialogue level | −37.5 LUFS | −16.7 LUFS |
| Explosion level | −14.1 LUFS | −11.7 LUFS |
| **Dialogue-to-explosion gap** | **23.4 LU** | **5.0 LU** |
| True peak | −5.5 dBTP | −1.6 dBTP |

Dialogue came up by more than 20 dB, and the gap between a whisper and an explosion fell
from 23 LU to 5.

---

## Requirements

- Jellyfin **10.11.x** or **12.x**.
- An ffmpeg with `loudnorm`, `ebur128`, `dynaudnorm` and `alimiter`. The bundled
  jellyfin-ffmpeg has all of them; the plugin uses the server's own binary, not the system one.
- **Media folders the server can write to.** Jellyfin only discovers external audio in the
  same folder as the video, so a read-only library cannot be served by this approach at all.
  The plugin checks this up front and says so rather than failing halfway through a job.

### Maturity

The plugin builds against the official Jellyfin packages for both lines and loads on 10.11
and 12. The audio pipeline is the well-tested part: every filter chain the plugin generates
has been pushed through real ffmpeg against real files and measured, and the numbers above
come from those runs. The Jellyfin integration around it has had far less mileage. Treat it
as young software.

---

## Install

### From the plugin repository

Dashboard → Plugins → Repositories → **+**, then paste:

```
https://raw.githubusercontent.com/caulfield33/audionormalizer-jellyfin/master/manifest.json
```

The plugin then appears in the catalogue and updates like any other. One manifest serves both
server lines: 10.11 gets the `1.0.0.x` build, 12 gets `2.0.0.x`.

### By hand

Download the zip matching your server from
[Releases](https://github.com/caulfield33/audionormalizer-jellyfin/releases), unpack it into
`<config>/plugins/Audio Normalizer/` and restart. Settings live at
**Dashboard → Plugins → Audio Normalizer**.

### From source

```bash
./build.sh           # Jellyfin 12.x    (needs the .NET 10 SDK)
./build.sh 10.11     # Jellyfin 10.11.x (needs the .NET 9 SDK)
./build.sh both
```

The zip lands in `artifacts/`. Pushing a `v<number>` tag makes the workflow build both
variants, publish them and regenerate `manifest.json`.

---

## Without the plugin

`tools/normalize-library.sh` is the same pipeline as a standalone script, for anyone who
would rather not install anything:

```bash
./tools/normalize-library.sh --dry-run /media/films   # see what it would do
./tools/normalize-library.sh /media/films             # do it
```

Then in Jellyfin: **Library → Scan**. Options: `--target -16`, `--range 9`, `--peak -1.5`,
`--codec eac3`, `--bitrate 384k`, `--force`, `--title "Night mode"`.

---

## How to use it

1. Open the plugin settings. The **System status** panel at the top shows the ffmpeg path,
   which filters are present, and warnings such as media folders that are not writable.
2. Press **Measure the whole library**. Nothing is written to disk; this only collects numbers.
3. The **Media report** fills in. The "Range" column is the quiet-to-loud spread per film;
   anything from 18 LU is highlighted, because that is where dialogue drowns in explosions.
4. **Click a film's name** to expand it. Every audio track is listed with its own measurements
   and a checkbox — tick the ones you want normalized. A film with Ukrainian, English and
   commentary tracks lets you pick exactly what you need. The choice saves immediately.
5. Then either **Build tracks where needed** in bulk, or **Build** on a single film.
6. In the player, switch the audio track to `AN - …`. The original stays where it was.

Each row in the report also has a **Settings** button, a per-film override of any global
value, and **Exclude**, which means never touch this film.

### The main thresholds

| Setting | What it is | Typical |
|---|---|---|
| Target loudness | the average level of the finished track | −16 LUFS |
| Maximum peak | the ceiling nothing goes above | −1.5 dBTP |
| Allowed range | how much dynamics to keep | 9 LU (night), 14 (cinematic) |
| Skip if range below | already-even audio gains nothing from a second track | 7 LU |

### Track naming

Generated tracks are called `AN - Ukrainian 5.1`: a configurable prefix followed by the
source track's own name. Several tracks from one film always get distinct file names.

---

## Why the defaults are what they are

Each of these was reproduced and measured before it was decided.

**`dynaudnorm` is the default engine.** A plain compressor only removes the loud parts and
never lifts the quiet ones — 24 → 20 LU, with dialogue no more audible than before.
`dynaudnorm` pulls quiet passages up: 23.4 → 9.4 LU. The compressor stays available as the
gentlest option for people who want to keep their dynamics.

**The stereo downmix is dialogue-forward, with LFE muted.** A plain `-ac 2` makes dialogue
*quieter*: measured −37.5 → −44.8 LUFS. The default matrix lifts the centre (+3 dB), pulls the
surrounds back (−3 dB) and drops LFE, where most of the "boom" lives. The whole matrix is
divided by the sum of its coefficients — unscaled it measured +6.2 dBTP after makeup gain.

**Filter order is load-bearing.** A compressor with an absolute dBFS threshold does nothing
at all against an attenuated downmix (measured: 23.9 LU, no reduction). So window-based
engines run **before** `loudnorm` and the classic compressor **after**, once the level is at
target and a dBFS threshold means something.

**`.mka` is the default container.** A raw `.ac3` keeps neither language nor title; `.m4a`
keeps the language but loses the title; `.mka` keeps both.

**The peak ceiling carries a 0.3 dB margin.** `alimiter` limits sample peaks while the target
is a true, inter-sample peak: a −1.5 dBFS limit measured −1.3 dBTP. With the margin it lands
at −1.6.

**Encoder delay is declared, not corrected.** Measured by transient onset: AAC +21.3 ms,
E-AC3 +5.3 ms, FLAC 0 ms. In every case the delay is correctly written into the container
(`start_time = −0.021`), so a compliant player compensates. For clients that do not, there is
a manual delay field, plus the choice of FLAC (zero delay) or E-AC3.

**The sample rate is always passed explicitly.** Left alone, `loudnorm` resamples its output
to 192 kHz — measured 69 MB instead of 17 MB for the same audio.

---

## What it takes care of

- Originals are never touched. Output is written to `*.an-part` and moved into place by an
  atomic rename, so Jellyfin never scans a half-written file.
- Track names survive Jellyfin's dot-parsing rules, which turn words like `forced`, `default`,
  `cc` and `hi` into flags and can erase a name entirely.
- Commentary and audio-description tracks are kept out of the automatic choice.
- External tracks are never offered as a source, so the plugin cannot be fed its own output.
- Free space is checked with a margin before each job, and there is a per-run size cap.
- The queue has a concurrency limit, runs under `nice`, and pauses while somebody is watching.
- Timeouts scale with the film's length; cancelling kills the process tree and leaves no debris.
- `state.json` fingerprints the source file and the settings, so nothing is rebuilt needlessly.
  It is written atomically, and a corrupted file is moved aside rather than breaking startup.
- Orphan cleanup only ever deletes paths the plugin recorded as its own output.
- Paths with spaces, brackets and Cyrillic work — arguments go through `ArgumentList`, never
  a shell.

---

## Limitations

- **This takes time.** On the test machine, processing ran roughly 13–16× faster than
  realtime, so a two-hour film is about 15–20 minutes including analysis. A 500-film library
  means days of background work. That is why the queue, the playback pause and the saved state
  are not decoration.
- **An external track disables container direct play.** To serve video together with external
  audio the server has to remux. The video is copied without re-encoding, so the cost is small
  but not zero.
- **Disk space.** AAC at 256 kbps is roughly 220 MB for a two-hour film; FLAC is 3–5× that.
- **Automatic strength is a heuristic.** The table mapping "how much to compress" onto the
  `dynaudnorm` window was fitted on a single synthetic clip. It is monotonic and behaves
  sensibly, but it does not guarantee hitting the requested range exactly — which is why the
  report shows the measured range per film and lets you override it.
- **5.1 output slightly breaches the ceiling** (−1.3 instead of −1.5 dBTP): the limiter works
  per channel while the measurement is of the downmix. The default stereo mode is within range.
- Films and episodes only. Music is left alone.

---

## When something doesn't work

Start with the **Diagnostics** button next to a film in the report. It shows the selected
tracks, the output path, write permissions, the measured numbers, and the exact ffmpeg command
you can paste into a terminal to reproduce the run by hand. The plugin logs every command it
runs at Debug level, and at Info if you turn that option on in settings.

---

## API

Every endpoint requires administrator rights.

| Method | Path | Purpose |
|---|---|---|
| GET | `/AudioNormalizer/Report?onlyProblems=true` | per-item report, including every track |
| GET | `/AudioNormalizer/Status` | queue status |
| GET | `/AudioNormalizer/Diagnostics` | ffmpeg, filters, warnings |
| GET | `/AudioNormalizer/SelfTest/{itemId}` | full dry self-test on one film |
| GET | `/AudioNormalizer/Libraries` | libraries, for a picker |
| POST | `/AudioNormalizer/Analyze` | queue measurement |
| POST | `/AudioNormalizer/Generate` | queue generation |
| POST | `/AudioNormalizer/Cancel` | empty the queue |
| POST | `/AudioNormalizer/Tracks` | choose which audio tracks of an item to normalize |
| POST | `/AudioNormalizer/Override` | per-item profile or exclusion |
| GET | `/AudioNormalizer/Override/{itemId}` | the effective profile for an item |
| DELETE | `/AudioNormalizer/Track/{itemId}?streamIndex=N` | delete a generated track |

```bash
curl -X POST 'http://localhost:8096/AudioNormalizer/Tracks' \
  -H 'Authorization: MediaBrowser Token="YOUR_TOKEN"' \
  -H 'Content-Type: application/json' \
  -d '{"ItemId":"<id>","StreamIndexes":[1,3]}'
```

Scheduled tasks (Dashboard → Scheduled Tasks): "Measure audio loudness",
"Build normalized audio tracks", "Remove orphaned normalized tracks".

---

## License

[MIT](LICENSE). Copyright (c) 2026 Vasyl Lukinchuk.

The software is provided "as is", without warranty of any kind. It writes files into your
media folders and runs ffmpeg across your library; the author is not liable for any claim,
damages or other liability arising from that.
