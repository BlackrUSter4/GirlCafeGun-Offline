# 05 — Real-phone port, English text, player package (2026-11-11)

Everything done to make this build run on a real, non-rooted phone (tested on a Samsung Galaxy S24+,
SM-S926B, Android 17) and to ship it to players without a PC. The emulator-era docs (01–03) still
describe the original design; this file lists what changed and why.

## 1. Player package (no PC, no root, no USB debugging)

Two files in the project root:

| File | Size | What |
|---|---|---|
| `GirlCafeGun.apk` | ~111 MB | the game + embedded server + first-run installer |
| `GirlCafeGun-data.gcg` | ~5.1 GB | all game data, one uncompressed tar |
| `HOW-TO-INSTALL.txt` | | player instructions |

Flow: install the APK → open the game → "Install game data" dialog → **Choose file** (system file
picker, no permission needed) → unpack → "Delete file" frees 5.1 GB → reopen and play.

- One file is not possible: an APK must stay under 4 GB (zip), and Android would keep a second copy.
- Installer: `apk-build/installer/gcg/offline/Installer.java`, compiled into the APK as an extra dex
  (`classes3.dex`; the base already has `classes2.dex`). No manifest change: it runs as a headless
  `android.app.Fragment` on the game's own activity and receives the picker result there.
- `apk-build/shim.c` `JNI_OnLoad` calls `gcg.offline.Installer.boot()`. `boot()` waits for
  `UnityPlayer.currentActivity`; the dialog appears when `Android/obb/<pkg>/` has no `.obb` over
  100 MB or `files/.gcg_data_installed` is missing.
- Data file format: tar (GNU), first entry `GCGDATA` (identifies the file, holds the data version),
  then `obb/<main obb>` → `getObbDir()` and `files/...` → `getExternalFilesDir(null)`. The installer
  checks free space, rejects other files, reports incomplete downloads, and writes the marker last.
- Tested: OBB hidden on the phone, real picker flow, unpack < 20 s, new OBB byte-identical,
  "Delete file" works, next start goes straight to the title with no missing bundles.
  Not yet tested on a phone that never had the game.

Build:
```
python apk-build/make_data_package.py      # -> GirlCafeGun-data.gcg
python apk-build/build_offline_apk.py      # -> GirlCafeGun.apk (base: gcg_offline_fixed.apk)
```
Tools (all under `C:\android-build`): Android NDK r27d, platform android-35, build-tools 35.0.1
(zipalign, apksigner, d8), Microsoft OpenJDK 21. adb in `C:\platform-tools`.

## 2. Why the original APK failed on a real phone

### 2.1 Embedded server crashed (SIGSEGV in `monovm_initialize`)
Real arm64 phones (Android 11+) tag heap pointers (TBI). The Mono runtime shifts a tagged pointer and
faults. Emulators have no TBI, so this never showed. Fix in `apk-build/host.c`: before hostfxr starts,
`mallopt(M_BIONIC_SET_HEAP_TAGGING_LEVEL, M_HEAP_TAGGING_LEVEL_NONE)` (logged as `heap tagging off`).

### 2.2 Client talked to Bilibili's servers ("Connection error. Please check if WiFi is turned on")
The client takes its server list from `assets/envir.json` **inside the OBB** (a copy in `files/` is
ignored). The shipped OBB had the stock file. `assets/obb/main...obb` is now patched in place
(same length, CRCs updated): the `en` environment points `serverlist` and `updateUrl` at
`http://127.0.0.1:9080`. The stock bytes are kept in `assets/obb/envir.json.stock`.

### 2.3 Saves were wiped on every launch
`shim.c` runs `rm -rf files/sssjsrv` and re-extracts the server on each start; saves lived in
`sssjsrv/data`. `host.c` now passes `--data-dir /data/data/<pkg>/files/sssjsave`, outside the wiped
folder (the server creates it).

### 2.4 Missing assets (magenta shaders, blank text, sky-only title, no videos, no voices)
- The OBB was not on the phone at all.
- `device_assets/added_bundles.txt` was incomplete: 2,409 more bundles from `assets/Bundles` that the
  OBB lacks are needed, plus `Raw/*.mp4` (intro and story videos) and `AutoAudio/` (12,757 voice/music
  files). The data package and `assets/sync_phone.ps1` now include all of them.
- The game rewrites `files/version.data` with the OBB's build-5 table after a broken boot; the merged
  `device_assets/version.data` must be restored.
- Not obtainable anywhere (by design or unused): 393 old `wwisedata/*.u` (replaced by AutoAudio),
  11 developer test scenes, 3 `update/*.u`.

### 2.5 Runtime/library notes
- The APK now ships the .NET runtime `.so` files that match the server publish (a newer .NET 10 patch
  release than the original build). The 7 runtime `.so` in `lib/arm64-v8a` and the data tar must
  always come from the same `dotnet publish`.
- Black bar on the camera side: Android letterboxes the cutout for this app. Player fix:
  Settings → Display → Camera cutout → Girl Cafe Gun → "Show camera cutout".

## 3. Client bundle work (files in `assets/device_assets/overrides`, installed into `files/`)

### 3.1 Encrypted Lua and settings (`lua.u`, `setting.u`)
`Game.BundleEncrypt.Decrypt` (libil2cpp RVA 0x1756d94): seed `0x166920ED`; for `i = 2, 6, 10, …`,
`j = i - 1`, if `seed % j < j / 3` swap `data[j]` and `data[i]`. The swap is its own inverse, so the
same routine encrypts. After decrypting, UnityPy reads the bundle normally.
The game CRC-checks `lua.u` against `versioncrc.json` (a `files/` copy overrides the OBB one); every
patched `lua.u` must ship with a matching `versioncrc.json` (CRC32, decimal).
Method addresses were found with a small own il2cpp v24.1 metadata reader (metadata methodIndex →
`CodeRegistration.methodPointers` at 0x4bd4a78, after applying R_AARCH64_RELATIVE relocations).

### 3.2 Title screen
- `UI_Login.lua` `Load2DBG` shows the hall title (`bg_login1` + `bg_login2`) when the language is
  English, else the sky title (`bg_login04` + the 4-girl animation). The 4-girl animation is a separate
  `Spine` object (`ShowConcertModel`, `girl1002_2da02`) that the script never touches; in the build-5
  OBB prefab it is on, so it was drawn over the hall title. The live game fixed this in
  build 179 by turning `Spine` off (the only byte that differs between the two prefabs).
- Now: `Load2DBG` picks sky or hall at random each launch and turns `Spine` on only for the sky.
- `ui/ui_login_prefab.u`: build-179 prefab with English labels (New Account, Enter Account, …) and
  Text best-fit enabled.

### 3.3 Uncensor switch
`files/l2d/girl111/love.u` (only its existence matters). Included in the data package by default.

## 4. English translation

- **Language table `lang.u`**: the official English build left 16,186 of 117,386 en_US values in
  Chinese (~389k characters: character stories solo5–16, Main Story 2, event stories, Railgun
  crossover, anniversary, UI, items, skills, gacha). All translated by parallel agents with a shared
  159-entry glossary built from the official English text; every entry checked for missing keys,
  leftover Chinese and changed placeholders/tags; drifted terms unified at merge ("Twin Horizons" →
  "Girl Cafe Gun", "Blackreach" → "Dark Sphere", Eksistere's 的说 → ", you know").
- **Scripts `lua.u`**: 65 hard-coded on-screen Chinese strings translated (80 replacements in 27
  files); 147 others left because they are keys, comparisons, logs or debug code.
- **Server** (`server/src`): 77 player-visible strings in English (bot friends, NPC clubs ≤ 12 chars,
  mails, chat notices, notice board, bag errors, debug-menu replies). Logs, internal maps and
  exceptions stay Chinese. Working files: the `tl` folder of the session scratchpad (not kept).
- Machine translation: some names of newer characters/places have no official English and were
  invented; quality may be uneven in places.

## 5. Tools and scripts added

| Path | Purpose |
|---|---|
| `apk-build/build_offline_apk.py` | compile shim/host/installer, repack, align, sign |
| `apk-build/make_data_package.py` | build `GirlCafeGun-data.gcg` |
| `apk-build/installer/` | first-run installer (Java) |
| `assets/sync_phone.ps1` | developer sync for a non-root phone over adb (OBB, bundles, audio, videos, overrides, love.u); the old `sync_device_assets.ps1` needs root |
| `assets/device_assets/overrides/` | patched `lang.u`, `lua.u`, `versioncrc.json`, `ui/ui_login_prefab.u` + README |
| `assets/Bundles/ui/ui_login_prefab.u.orig` | untranslated build-179 prefab |
| `gcg_offline_fixed.apk` | previous APK without the installer (server + runtime fixes only) |

## 6. Bug review (2026-11-11)

Fixed: non-root sync (old script failed silently without `su`), overrides missing from sync, NPC club
names over the 12-character limit, full-width colon in a debug reply, `mallopt` result not logged.
Checked fine: duplicate-mail detection does not use mail text; bot/club names unique; save path;
`mallopt` ordering; CRC values.
