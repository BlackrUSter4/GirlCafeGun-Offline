# Girl Cafe Gun — Offline (EN v1.0.9)

An **unofficial, offline, single-player** build of Girl Cafe Gun. The game server is
**embedded inside the APK** — install it and play with **no account, no internet, and no PC**.
Everything stays on the device; it works in full **airplane mode**.

> **This repository ships no game assets.** The game, its art/audio/text, and the client
> belong to **Seasun (西山居)** and its publishers. You provide the game files yourself (from a
> copy of the official app you own), delivered as a separate **asset pack** you host. See
> **[CREDITS.md](CREDITS.md)** for full attribution and IP notice, and
> **[docs/project.html](docs/project.html)** for an overview + the limitations table.

For preservation and study only. **Scan anything you download before running it.**

---

## Quick start — just play

1. **Get the asset pack** (the ~6 GB of game files needed to play) and unzip it so that
   `gcg_offline.apk` and the `assets/` folder sit next to **`INSTALL.bat`**.
2. Start an **arm64** Android emulator — [MuMu Player 12](https://www.mumuplayer.com/) with
   **Root** on is easiest — or connect an arm64 phone with USB debugging.
3. Double-click **`INSTALL.bat`**.

It installs the APK, puts the OBB in place, syncs the content bundles, and launches the game.
First launch shows a Terms screen → **Agree to all & start**; at the title (server shows
**Local**) tap **New Account** and play. No internet needed.

Full step-by-step: **[docs/01-PLAY.md](docs/01-PLAY.md)**.

### Using a hosted download link

The asset pack is too large (and too copyrighted) to live in this repo, so **you host it** and
point the installer at it:

```powershell
# one time: build the pack, then upload dist\gcg_assets.* to your host (Drive / MEGA / torrent)
powershell -ExecutionPolicy Bypass -File scripts\make-asset-pack.ps1

# players then run:
INSTALL.bat -AssetPackUrl https://your-host/gcg_assets.zip
```

The installer downloads the pack, extracts it, and installs — so a single link is all a player
needs from you.

---

## What's in this repo

```
GirlCafeGun-Offline/
├─ INSTALL.bat              ← one-click installer (double-click, or pass -AssetPackUrl / -Device)
├─ scripts/
│   ├─ install.ps1           the installer logic (APK → OBB → bundle sync → launch)
│   ├─ build-server.ps1      publish the server for Android (arm64, self-contained .NET)
│   ├─ build-apk.ps1         bake gcg_offline.apk from source (needs your stock APK)
│   └─ make-asset-pack.ps1   package the player asset pack you host
├─ apk-build/              build tooling (shim.c, host.c, repack.py, patch_il2cpp.py, key)
├─ server/                 the embedded server — sssj-new source (MIT) + setting/ (not committed)
├─ docs/
│   ├─ project.html          project overview + limitations (open in a browser)
│   ├─ 01-PLAY.md            install + play, with troubleshooting
│   ├─ 02-RUN-SERVER.md      run the server standalone (dev)
│   ├─ 03-BUILD-APK.md       rebuild the offline APK from source
│   ├─ 04-NO-ROOT.md         install the bundles/assets without root (Shizuku etc.)
│   └─ 05-UNCENSOR.md        the love.u uncensor patch (restores Live2D interactions)
├─ assets/                 game files — NOT committed (see .gitignore); delivered via the pack
│   ├─ sync_device_assets.ps1   pushes the content bundles + merged table to the device
│   └─ device_assets/           added_bundles.txt (the bundle list) + version.data
├─ CREDITS.md              attribution + intellectual-property notice (read this)
└─ LICENSE                 MIT — covers this repo's original work only
```

What's intentionally **not** here (it's game-owned, delivered in the asset pack): the OBB, the
asset `Bundles/`, Live2D models, the built `gcg_offline.apk`, `server/setting/`, and any Seasun
client binaries. The build scripts read those from *your* stock APK / pack at build time.

---

## How it works (short version)

A shim replaces the game's tiny `libmain.so` and, on launch, starts the bundled .NET server as a
local process, points the client at `http://127.0.0.1:9080`, and patches the client's
offline/airplane gates in memory. The whole server — runtime + the open-source `sssj-new` server +
the config tables — ships inside the APK. **No root needed**: the runtime runs from the app's
native-library dir, not the SELinux-blocked data dir. Details: **[docs/project.html](docs/project.html)**
and **[docs/03-BUILD-APK.md](docs/03-BUILD-APK.md)**.

## Build it yourself

```powershell
# server only (native Android arm64 build):
powershell -ExecutionPolicy Bypass -File scripts\build-server.ps1

# full offline APK (needs your own stock base.apk, the .NET 10 SDK, Android NDK r27 + build-tools):
powershell -ExecutionPolicy Bypass -File scripts\build-apk.ps1 -OrigApk C:\path\to\base.apk
```

Re-signing uses the included debug key, so you can update an install in place with `adb install -r`.

## Limitations (read before expecting everything to work)

| Area | Status |
|------|--------|
| Offline play / airplane mode | ✅ Works (fresh account, tutorial, story, gacha, shop) |
| "Connection error" on New Account | ✅ Fixed in this build (network gate patched) |
| Missing voice / scenes / art | ✅ Fixed — the full bundle set the game references now ships |
| Censored Live2D touch/flirting interactions | ✅ Restored by default (love.u) — [docs/05-UNCENSOR.md](docs/05-UNCENSOR.md); `-NoUncensor` to opt out |
| Chinese title buttons (新账号 / 输入账号) | ⚠️ Known — baked into the EN login-UI prefab; not yet translated |
| Background telemetry calls | ⚠️ Harmless — fail silently offline; don't block play |
| Non-rooted phones (asset push) | ⚠️ Works via Shizuku / file manager — see [docs/04-NO-ROOT.md](docs/04-NO-ROOT.md) |
| Official online services / multiplayer | ❌ Out of scope by design |

Full table + explanations: **[docs/project.html](docs/project.html)**.

## Credits

- **Game:** Girl Cafe Gun © **Seasun Games (西山居 / Kingsoft)** and its publishers. Unofficial;
  not affiliated or endorsed; no assets distributed.
- **Server:** [`sssj-new`](https://github.com/SteveS233) by **SteveS233** — MIT, © 2025 luoxiadesu.
- **This repo's** tooling, scripts and docs: MIT (see [LICENSE](LICENSE)).

See **[CREDITS.md](CREDITS.md)** for the complete notice and responsible-use terms.
