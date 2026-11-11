# Girl Cafe Gun — Offline (EN v1.0.9)

A fully offline build of Girl Cafe Gun. The game server is **embedded inside the APK** — install it and
play, with no account, no internet, and no PC. This package also includes the server source so you can
run or modify the server yourself.

> **This build — rebuilt 2026-11-10.** `gcg_offline.apk` embeds the current server source, which carries
> the Android fixes it needs (managed **BouncyCastle** 3DES for the login gate and `Random.Shared` for
> key generation, so the runtime doesn't abort on OpenSSL init) plus the settle-validation hardening in
> `ChapterHandler` (a chapter result is only accepted on the stock path when it carries neither a battle
> token nor a `bSuccess` flag). The whole server test suite is green. SHA-256 of this APK's signing cert:
> `a8c4ecec977351dc617365d51644065efbd76aaddeb2f0bf9db9865b7db12e71` (self-signed "SSSJ Offline").
> Re-signed with the same key as prior builds, so `adb install -r` updates an existing install in place.

## What's in here

```
GCG2-Offline/
  gcg_offline.apk        ← install this on an Android emulator/device to play offline
  server/                ← the standalone game server (.NET source) + game data
    src/                   the 4 projects (App / Core / Game / Protocol)
    SssjServer.sln
    setting/               the game config tables the server reads (EN v1.0.9)
  assets/                ← game assets needed to play (see docs/01-PLAY.md)
    obb/                   the OBB asset pack (~1.7 GB) → the app's obb folder
    Bundles/               full asset-bundle repository (5.2 GB)
    live2d-models/         Live2D models (3.1 GB, reference)
    device_assets/         version.data + added_bundles.txt (what the game needs beyond the OBB)
    sync_device_assets.ps1 pushes the needed bundles + version.data to the device
  apk-build/             ← sources used to build the offline APK (shim, host, repack)
  docs/
    01-PLAY.md             install the APK + assets and play
    02-RUN-SERVER.md       build and run the server yourself (PC / dev)
    03-BUILD-APK.md        rebuild the embedded offline APK from source
```

## Pick your path

| I want to… | Read |
|-----------|------|
| **Just play offline** | [docs/01-PLAY.md](docs/01-PLAY.md) — install `gcg_offline.apk`, done |
| **Run the server myself** (dev, testing, modding) | [docs/02-RUN-SERVER.md](docs/02-RUN-SERVER.md) |
| **Rebuild the offline APK** | [docs/03-BUILD-APK.md](docs/03-BUILD-APK.md) |

## How the offline APK works (in one paragraph)

The APK ships the whole game server (a self-contained .NET runtime + the server + the config data)
inside it. A small shim (`libmain.so`) added to the app starts the server as a local process when the
game launches, points the client at `http://127.0.0.1:9080`, and takes the offline-account path — so
the client talks to the server running on the same device. Nothing leaves the phone.

## Requirements to **play**
- An **arm64** Android emulator (MuMu Player 12 recommended) or arm64 phone. **Root is not required** —
  the embedded server runs out of the app's native-library dir, which Android lets apps execute. A
  rooted emulator is still the easiest way to push assets (the sync script uses it).
- **No network required** — the build plays in full **airplane mode**. Everything stays on `127.0.0.1`;
  nothing leaves the phone. (The client's offline-blocking "check your network" gate is patched out.)
- `adb` + PowerShell on your PC. The assets you need are in `assets/` (included) — see
  [docs/01-PLAY.md](docs/01-PLAY.md) for the install + sync steps.

> **Non-rooted phones:** the server itself needs no root (proven end-to-end: fresh account, encrypted
> gate session, tutorial). The open piece is asset delivery without root — the OBB pushes to the public
> `Android/obb/` folder fine, and the extra bundles go to the app's external files dir. See the
> non-rooted section of [docs/03-BUILD-APK.md](docs/03-BUILD-APK.md).
