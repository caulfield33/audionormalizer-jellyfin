# Checklist

What to do with this code, in order. Steps 1–5 are required, the rest as needed.

---

## 1. Prepare the repository

- [ ] Unpack the archive somewhere permanent (not Downloads).
- [ ] `git init && git add . && git commit -m "Audio Normalizer"`
- [ ] Create the repository on GitHub. **Public** — then GitHub Actions is free with no limit.
- [ ] `git remote add origin ... && git branch -M main && git push -u origin main`
- [ ] Settings → Actions → General → Workflow permissions → **Read and write permissions** → Save.

> Without the last step the build succeeds but the workflow cannot commit `manifest.json`.

## 2. First release

- [ ] `git tag v1 && git push origin v1`
- [ ] The **Actions** tab — wait for the green tick (about 2 minutes).
- [ ] Check that **Releases** holds two zips and that `manifest.json` is in the repository root.

**If the build fails**, this is the most likely point where a fix is needed. See step 7.

## 3. Connect it to Jellyfin

- [ ] Dashboard → Plugins → Repositories → **+**
  - Name: `Audio Normalizer`
  - URL: `https://raw.githubusercontent.com/YOUR_USER/YOUR_REPO/main/manifest.json`
- [ ] Dashboard → Plugins → Catalog → Audio Normalizer → Install.
- [ ] Restart Jellyfin.
- [ ] Dashboard → Plugins → Audio Normalizer opens, and the **System status** panel at the top
      shows the ffmpeg path with no red warnings.

## 4. First run — carefully

Do not start on the whole library.

- [ ] Turn on **Dry run** in the settings and save.
- [ ] Press **Measure the whole library**. Nothing is written to disk.
- [ ] Look at the report: the "Range" column shows which films actually have the problem
      (18+ LU is highlighted). The summary above the table estimates total time and disk space.
- [ ] Pick **one** problem film → **Diagnostics**. Confirm the right audio track is selected
      and the folder is writable.
- [ ] Click the film's name to expand it and tick exactly the tracks you want normalized.
- [ ] Turn off Dry run.
- [ ] Press **Build** on that same film.
- [ ] Wait, open the film in the player, switch the audio to `AN - …`, and listen.

## 5. Tune it to taste

Having heard the result:

- [ ] Too flat or unnatural → raise "Allowed range" to 12–14 LU.
- [ ] Dialogue still quiet → drop "Allowed range" to 6–7, centre to +5 dB, LFE at −60.
- [ ] Audio drifts against the video → switch the codec to FLAC or E-AC3.
- [ ] Check 2–3 more films before running in bulk.

## 6. Run it across the library

- [ ] Estimate the time: roughly 15–20 minutes per two-hour film. 100 films ≈ a day.
- [ ] Check free space: about 220 MB per film at AAC 256k.
- [ ] Make sure "Pause while somebody is watching" is on.
- [ ] Press **Build tracks where needed**.
- [ ] Leave it running. Progress is visible in the Queue panel.

## 7. If something goes wrong

- [ ] The **Diagnostics** button next to the film — it holds the exact ffmpeg command and output.
- [ ] `grep "Audio Normalizer" /config/log/log_*.log | tail -50`
- [ ] Full guide: [DEBUGGING.md](DEBUGGING.md).
- [ ] If the code needs changing, open the project in Claude Code — it reads
      [CLAUDE.md](CLAUDE.md) and has the whole context.

## 8. Fallback without the plugin

If building the plugin is taking too long and you want the audio fixed now:

- [ ] `./tools/normalize-library.sh --dry-run /media/films`
- [ ] `./tools/normalize-library.sh /media/films`
- [ ] Scan the library in Jellyfin.

The same pipeline, the same file naming, no C# and no build.

---

## What you do NOT need to do

- Don't install .NET locally — GitHub does the building.
- Don't edit `manifest.json` by hand — the workflow generates it.
- Don't touch `build/stubs/` — those are stubs for offline type-checking and play no part in
  a release.
- Don't run across the whole library before hearing the result on at least one film.
