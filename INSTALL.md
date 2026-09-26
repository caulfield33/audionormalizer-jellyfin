# Installing

**Short version: push the code to GitHub, tag it, add one URL in Jellyfin. Nothing needs to
be compiled locally — GitHub does the building.**

A local build is described below, but you only need it if you would rather not use GitHub.

---

## Option A. Through GitHub (recommended)

You need no .NET SDK, nothing to build, and no terminal at all after the push. GitHub Actions
does the work.

### One time

1. **Push the project to a GitHub repository.**

   ```bash
   cd audionormalizer
   git init
   git add .
   git commit -m "Audio Normalizer"
   git branch -M main
   git remote add origin https://github.com/YOUR_USER/jellyfin-audio-normalizer.git
   git push -u origin main
   ```

2. **Give Actions write permission.**
   Settings → Actions → General → Workflow permissions → **Read and write permissions** → Save.

   Without this the workflow builds the plugin but cannot commit the updated `manifest.json`.

3. **Push a tag.**

   ```bash
   git tag v1
   git push origin v1
   ```

   Everything after this happens on GitHub's servers, in about a minute:

   ```
   git push origin v1
        │
        ├─ installs .NET 9 and .NET 10
        ├─ builds for Jellyfin 10.11  -> audio-normalizer_1.0.0.1.zip
        ├─ builds for Jellyfin 12     -> audio-normalizer_2.0.0.1.zip
        ├─ publishes both as a GitHub Release tagged v1
        ├─ generates manifest.json pointing at them, with md5 checksums
        └─ commits manifest.json back to main
   ```

   Watch it run on the repository's **Actions** tab.

4. **Add the repository in Jellyfin.**
   Dashboard → Plugins → Repositories → **+**

   | Field | Value |
   |---|---|
   | Name | `Audio Normalizer` |
   | URL | `https://raw.githubusercontent.com/YOUR_USER/jellyfin-audio-normalizer/main/manifest.json` |

5. **Dashboard → Plugins → Catalog** → Audio Normalizer → Install → restart the server.

### Every later update

```bash
git add . && git commit -m "what changed"
git push
git tag v2 && git push origin v2
```

Jellyfin notices the new version in the catalogue on its own and offers the update.

### What it costs

Nothing. **For public repositories, GitHub Actions on standard runners is free with no minute
limit.** If you make the repository private, the free plan includes 2,000 minutes a month
(Pro: 3,000), and one release of this plugin is roughly 3–6 minutes for both builds together.
Even privately that is enough for hundreds of releases.

Your minute counter: Settings → Billing → Plans and usage.

### About the two builds

With CI, supporting both Jellyfin lines costs you nothing: you don't run those builds, GitHub
does, in parallel, from two rows of a matrix. Your server takes only the one it needs from the
manifest — see the end of this file.

---

## Option B. By hand, without GitHub

You need a .NET SDK: 10 for Jellyfin 12, 9 for Jellyfin 10.11.

```bash
./build.sh          # for Jellyfin 12.x
./build.sh 10.11    # for Jellyfin 10.11.x
```

A zip appears in `artifacts/`. Unpack it into your Jellyfin config so that you end up with
**exactly** this:

```
<config>/plugins/Audio Normalizer/Jellyfin.Plugin.AudioNormalizer.dll
<config>/plugins/Audio Normalizer/meta.json
```

Where `<config>` is:

| Installation | Path |
|---|---|
| Docker | the volume mounted at `/config` |
| Debian/Ubuntu package | `/var/lib/jellyfin` |
| Windows | `%ProgramData%\Jellyfin\Server` |

A common mistake is an extra nested folder
(`plugins/Audio Normalizer/audio-normalizer_2.0.0.0/...`). The plugin will not load.

For Docker, in one line:

```bash
mkdir -p /path/to/config/plugins/"Audio Normalizer"
unzip -o artifacts/audio-normalizer_2.0.0.0.zip -d /path/to/config/plugins/"Audio Normalizer"
docker restart jellyfin
```

### Your own manifest without GitHub Actions

If you want catalogue updates but no CI:

```bash
RELEASE=1 ./build.sh both
# put artifacts/*.zip somewhere reachable over HTTP, then:
./tools/make-manifest.sh \
  "https://example.com/plugins/v1" \
  "https://example.com/plugins/manifest.json"
```

The manifest can be hosted anywhere that serves it as a plain file: GitHub Pages, your own
nginx, even a local HTTP server on your network.

---

## Why the manifest lists two versions, and how Jellyfin chooses

Jellyfin 12 and 10.11 need different builds (net10.0 and net9.0). The server keeps only those
manifest entries whose `targetAbi` is not higher than its own version, then installs the
**highest** plugin version remaining. Hence:

| Build | Plugin version | targetAbi |
|---|---|---|
| for Jellyfin 10.11 | `1.0.0.x` | `10.11.0.0` |
| for Jellyfin 12 | `2.0.0.x` | `12.0.0.0` |

Which gives:

```
Jellyfin 10.11.11  -> installs 1.0.0.x   (the 12 entry is filtered out)
Jellyfin 12.1.0    -> installs 2.0.0.x   (higher version wins)
Jellyfin 10.9      -> nothing is offered
```

The `targetAbi` check runs twice: at install time and again when the plugin loads.

**Worth understanding where this mechanism stops.** Jellyfin never checks which .NET version
a DLL was built for — `meta.json` has no such field (the `framework` value `build.sh` writes
is ignored by the server; it is a note for humans). Correctness rests entirely on the version
numbering convention. Break it and the server will happily load the wrong build — but it
breaks loudly and safely: the log shows `Failed to load assembly ... Disabling plugin`, the
plugin is marked `Malfunctioned`, and neither your library nor your files are affected.

`RELEASE=n` changes only the last component: `RELEASE=2 ./build.sh both` produces `1.0.0.2`
and `2.0.0.2`. In CI this comes from the tag automatically (`v2` → `RELEASE=2`).

**If you run one server and don't plan to migrate**, don't bother with any of this: build for
your version and the manifest will hold a single entry with no numbering tricks at all.

---

## Checking that it installed

Dashboard → Plugins → Audio Normalizer should open a settings page with a **System status**
panel at the top showing the ffmpeg path.

If the plugin is not in the list, check the log from startup:

```bash
grep -iE "plugin|audionormalizer" /config/log/log_*.log | head -30
```

The usual causes:

- built for the wrong Jellyfin version (`targetAbi` in `meta.json` does not match the server);
- an extra nested folder inside `plugins/Audio Normalizer/`;
- stray DLLs in the plugin folder shadowing the server's own — there should be only
  `Jellyfin.Plugin.AudioNormalizer.dll` and `meta.json`.

Next stop: [DEBUGGING.md](DEBUGGING.md).
