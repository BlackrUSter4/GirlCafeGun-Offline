# 01 — Play offline

The `gcg_offline.apk` has the server baked in. You only need to install it, give it the game's asset
pack (OBB), and launch.

## Requirements
- An **arm64** Android emulator or device. [MuMu Player 12](https://www.mumuplayer.com/) is what this
  was built and tested on. **Root is not required to run the game** — but the asset-sync script below
  uses root to push files, so on MuMu enabling **Root** is the simplest path. **No root?** On a phone use
  the player package (`GirlCafeGun.apk` + `GirlCafeGun-data.gcg`, see
  [../HOW-TO-INSTALL.txt](../HOW-TO-INSTALL.txt)) or `assets\sync_phone.ps1` over adb; the root-based
  `sync_device_assets.ps1` fails silently there. Details:
  [05-PHONE-PORT.md](05-PHONE-PORT.md). The Shizuku / file-manager routes are in
  **[04-NO-ROOT.md](04-NO-ROOT.md)**.
- **No network needed — airplane mode is fine.** Everything the game does stays on `127.0.0.1`
  (nothing leaves the device). The client's offline-blocking "check your network" gate is patched out,
  so New Account works even with Wi-Fi, data, and everything off.
- `adb` (Android platform-tools) on your PC, and **PowerShell** (for the asset sync script).
- The assets in `../assets/` (included): the OBB, the bundle repository, `version.data`, and
  `sync_device_assets.ps1`. (Paths below assume you run from `GCG2-Offline/`.)

## The assets, briefly
Offline play needs two things on the device beyond the APK:
1. the **OBB** (`assets/obb/…obb`, ~1.7 GB) → goes in the app's obb folder (step 3),
2. a set of newer **bundles** the current content needs beyond the OBB (listed in
   `assets/device_assets/added_bundles.txt`, ~557 MB) plus the merged `version.data` → pushed into the
   app's files folder by `assets/sync_device_assets.ps1` (step 4).

`assets/Bundles/` (the full 5.2 GB repository) and `assets/live2d-models/` are included for
completeness; normal play only uses the OBB + the bundles the sync script installs.

## Steps

1. **Connect to your emulator/device.** For MuMu the address is usually `127.0.0.1:16448` (check
   MuMu's ADB setting). Replace `$D` below with yours.
   ```bash
   adb connect 127.0.0.1:16448
   adb devices                     # should list it as "device"
   ```

2. **Install the APK.**
   ```bash
   adb -s 127.0.0.1:16448 install gcg_offline.apk
   ```
   If an official copy of the game is already installed, uninstall it first
   (`adb uninstall com.biligameglobal.girlcafegungp`) — the signatures differ.

3. **Put the OBB in place** (as root, since the app's obb folder needs it):
   ```bash
   D=127.0.0.1:16448; P=com.biligameglobal.girlcafegungp
   adb -s $D push assets/obb/main.10009.$P.obb /data/local/tmp/main.obb
   adb -s $D shell "su 0 sh -c 'mkdir -p /sdcard/Android/obb/$P; cp /data/local/tmp/main.obb /sdcard/Android/obb/$P/main.10009.$P.obb; chmod 666 /sdcard/Android/obb/$P/main.*.obb'"
   ```

4. **Sync the extra bundles + the merged asset table.** From `GCG2-Offline/`, run (PowerShell):
   ```powershell
   powershell -File assets\sync_device_assets.ps1 -Device 127.0.0.1:16448
   ```
   It installs the merged `version.data` and the ~380 bundles the current content needs beyond the OBB,
   straight into the app's files folder (over `adb` + root). It prints `device assets installed` (or
   `device assets up to date`). Re-running it is safe; it only pushes what's missing.

5. **Launch.** Tap the Girl Cafe Gun icon, or:
   ```bash
   adb -s 127.0.0.1:16448 shell "am start -n com.biligameglobal.girlcafegungp/com.crasheye.client.api.unity3d.MainActivity"
   ```

The first launch shows a Terms-of-Use dialog (tap **Agree to all & start**). At the title screen the
server shows as **Local**. Tap **新账号 (New Account)** → **Tap Screen to Start** to create a fresh
account and begin the tutorial. Everything runs on the device — no internet needed.

## Troubleshooting
- **Stuck on the splash / server never responds:** first confirm the embedded server came up — on a
  rooted emulator, read its log at `/data/data/com.biligameglobal.girlcafegungp/files/sssjsrv/srv.log`
  (it ends with `引导服务监听 0.0.0.0:9080` + `监听 0.0.0.0:9600` when healthy). If the server is up but the
  client sits on the splash, the usual cause is **missing asset bundles**: the Unity log shows
  `Assets.LoadBundle missing bundle : <name>` and the game can't finish loading. Run the sync in step 4
  (`sync_device_assets.ps1`) so the bundles are on the device. Also confirm you launched this APK (not an
  official copy) and left no stale `adb reverse tcp:9080` (`adb reverse --remove-all`).
- **Boot gets very slow after many offline sessions:** the client bundles a crash/telemetry SDK
  (Crasheye) that queues one "session" per launch and keeps retrying to upload them. Offline, those never
  send, so after ~a dozen offline launches the backlog makes the uploader thread peg a CPU core and starve
  the game's main thread — the splash crawls. It's harmless (nothing leaves the device either way) but
  annoying. Clear it with `adb -s <device> shell pm clear com.biligameglobal.girlcafegungp` — note this
  also wipes the local account and re-triggers the first-launch Terms screen + asset sync (re-run step 4).
- **"Missing prefab" / black screen after login:** the OBB or the bundle table is stale. Reinstall the
  OBB from step 3; if you have the project's `sync_device_assets.ps1` + `device_assets/version.data`,
  run it to install the merged bundle table.
- **Nothing happens on launch:** confirm the install succeeded and you launched the right activity
  (`com.crasheye.client.api.unity3d.MainActivity`).
