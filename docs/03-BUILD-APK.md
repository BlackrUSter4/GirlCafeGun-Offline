# 03 — Rebuild the offline APK

How the self-contained `gcg_offline.apk` is made, in case you change the server and want to re-bake it.
The build sources are in `apk-build/`.

## How it works
A shim replaces the game's tiny `libmain.so` (Unity's JNI entry) and, on launch:
1. **chains** the original libmain so the game boots normally,
2. **extracts** the bundled server to the app's data dir, **symlinks the runtime `.so` out of the app's
   native-library dir** (the exec-allowed folder — this is what makes it work without root), and
   **forks it as a separate process** (the .NET runtime cannot share the app's process with Unity's
   il2cpp — it aborts), hosting it via `hostfxr`,
3. **patches il2cpp in memory** (read via `/proc/self/mem`, which can't SIGSEGV) so the client uses
   `http://127.0.0.1:9080` with the offline-account path; the patch runs in a **continuous re-apply
   loop** (~5 min) so `disablegm=0` + `platform=selftest` survive the client's own `PackageInfo.Load`
   overwrite right up until it builds the serverlist URL, even on a slow cold boot,
4. the client then talks to the embedded server on `127.0.0.1:9080` (http bootstrap) / `9600` (gate).

Everything (the .NET runtime, the server DLLs, and the `setting/` tables) is packed into one file,
`lib/arm64-v8a/libsssjdata.so` (a gzip tar), plus the shim, the host, and the runtime `.so` — which
ship as their own `lib/arm64-v8a/*.so` entries so Android installs them into the exec-allowed native
library dir.

## Build sources (`apk-build/`)
- `shim.c` → compiled to `libmain.so` (the shim: fork the host, patch il2cpp, chain libmain).
- `host.c` → compiled to `libsssjhost.so` (hosts the server via hostfxr in its own process).
- `repack.py` → zip surgery: from a pristine stock APK (`orig.apk`), drop the old `libmain.so` + the
  old signature, add the new `lib/arm64-v8a/*` entries.
- `sssj.keystore` → the signing key (store/key password `sssjsssj`, alias `sssj`).
- `libmain_orig_arm64.so` → the stock Unity libmain (shipped inside the APK as `libmain_orig.so`).

## Tools needed
- **.NET 10 SDK** (to publish the server for Android).
- **Android NDK r27** (clang) and **build-tools** (`zipalign`, `apksigner`) — from Android Studio /
  the Android SDK.
- **Python 3** (for `repack.py`).
- A copy of the **stock** `com.biligameglobal.girlcafegungp.apk` named `orig.apk` next to `repack.py`.

## Steps (from `apk-build/`)
```bash
NDK=/path/to/Android/Sdk/ndk/27.2.12479018
CC=$NDK/toolchains/llvm/prebuilt/<host>/bin/aarch64-linux-android21-clang
BT=/path/to/Android/Sdk/build-tools/35.0.0

# 0. publish the server as a native Android binary (JIT runtime, NOT AOT)
dotnet publish ../server/src/SssjServer.App/SssjServer.App.csproj -c Release \
  -r linux-bionic-arm64 --self-contained true -p:PublishAot=false
PUB=../server/src/SssjServer.App/bin/Release/net10.0/linux-bionic-arm64/publish

# 1. data tar = the WHOLE publish (apphost + .so + .dll + the libmono-*.a / libmonosgen-2.0.a !) + setting, minus .pdb
mkdir -p staging lib/arm64-v8a
cp "$PUB"/SssjServer.App "$PUB"/*.so "$PUB"/*.dll "$PUB"/*.json "$PUB"/*.a staging/
cp -r ../server/setting staging/setting
tar -C staging -czf lib/arm64-v8a/libsssjdata.so .

# 1b. the 7 runtime .so ALSO ship as their own lib-dir entries (Android installs them to the
#     exec-allowed native-library dir; the shim symlinks them next to the DLLs at runtime).
for so in libhostfxr libhostpolicy libcoreclr libSystem.Native \
          libSystem.Globalization.Native libSystem.IO.Compression.Native \
          libSystem.Security.Cryptography.Native.OpenSsl; do
  cp "$PUB/$so.so" lib/arm64-v8a/
done

# 1c. (optional, for full airplane-mode play) patch the client's reachability gate. Copy the stock
#     arm64-v8a libil2cpp.so out of orig.apk and force Game.Utils.IsNetworkOk + Login.IsNetworkReachability
#     to return true, so "New Account" works with NO network. (See "Airplane mode" below for why we patch
#     only these two, not the global getter.) Shipping it as a lib-dir entry makes repack.py replace the
#     stock one.
python - <<'PY'
import zipfile
zipfile.ZipFile("orig.apk").extract("lib/arm64-v8a/libil2cpp.so", ".")
PY
python patch_il2cpp.py lib/arm64-v8a/libil2cpp.so

# 2. compile the shim + host (android30 clang; the shim needs -landroid for __android_log too)
"$CC" --target=aarch64-linux-android30 -shared -fPIC -O2 -o lib/arm64-v8a/libmain.so    shim.c -llog -landroid
"$CC" --target=aarch64-linux-android30 -fPIE -pie  -O2 -o lib/arm64-v8a/libsssjhost.so  host.c -llog -ldl
cp libmain_orig_arm64.so lib/arm64-v8a/libmain_orig.so

# 3. repack, align, sign
python repack.py          # needs ./orig.apk (the pristine stock APK)
"$BT/zipalign" -f -p 4 gcg_offline_unsigned.apk gcg_offline_aligned.apk
"$BT/apksigner" sign --ks sssj.keystore --ks-pass pass:sssjsssj --ks-key-alias sssj --key-pass pass:sssjsssj \
  --out gcg_offline.apk gcg_offline_aligned.apk
```

Re-signing with the same key means you can update an already-installed copy with `adb install -r`
(no uninstall, keeps the OBB and save data).

## Gotchas (learned the hard way)
- The `.a` files (`libmono-component-*.a`, `libmonosgen-2.0.a`) **must** be in the data tar or the
  runtime aborts with *"corlib not met"*.
- Do **not** use the stock apphost's self-location — under the emulator's arm translation
  (`/proc/self/exe` resolves to a translation path); host via `hostfxr` with explicit paths instead.
- Read il2cpp memory with `/proc/self/mem` (`O_RDONLY` + `pread`), never a raw dereference — a direct
  read near the end of the 84 MB lib SIGSEGVs under translation, and `process_vm_readv` on self is
  flaky under the emulator's arm translation (returns garbage). `/proc/self/mem` is the kernel's own
  view and is reliable. Writes target the managed heap, so a guarded `memcpy` is fine.
- Patch in a **re-apply loop**, not once. The client's `PackageInfo.Load` rewrites `platform` back to
  its online value after a one-shot patch; re-writing `disablegm=0` + `platform=selftest` every ~30 ms
  for ~5 min (re-resolving `PackageInfo.Instance` each pass) makes the values stick until the client has
  built the serverlist URL — the long window covers a pathologically slow cold boot where `Instance`
  initializes tens of seconds in.
- Set `disablegm=0` **early** (the loop does, within ~1 s of il2cpp mapping). With it clear before the
  URL is built, the client never upgrades `http://127.0.0.1:9080` to `https`, so no socket surgery is
  needed — don't kill the `:9080` socket (it drops the good http connection).

## Non-rooted phones — the device needs a live network (even offline)
The server runs out of the app's **native-library** dir, not the data dir, so it does **not** need
root: Android marks the lib dir exec-allowed, and the shim symlinks the runtime `.so` there before
forking the host. (The data dir, which stock arm64 / targetSdk 30+ blocks from executing under
SELinux, only holds the DLLs + `setting/` — no exec.) This was proven end-to-end on an arm64 emulator:
fresh account, encrypted gate session, tutorial.

### Airplane mode (the libil2cpp patch, step 1c)
The client's **New Account** handler calls `Game.Utils.IsNetworkOk()` — which returns
`Application.internetReachability != NotReachable` — and shows *"Connection error. Please check your
network."* when it's false, **before** it ever touches the local server. All game traffic is localhost,
so the check is pointless offline. Step 1c patches `IsNetworkOk` (RVA `0x14F48DC`) and the login-screen
variant `Game.Login.IsNetworkReachability` (RVA `0x1BEBDA4`) to `mov w0,#1; ret` (always true). With
that, a fresh account logs in and plays the tutorial with the device in **full airplane mode** — proven
end-to-end.

Patch *only* those two helpers — **not** the underlying `UnityEngine.Application::get_internetReachability`
(RVA `0x3064888`). The boot sequence also consults reachability; forcing the global getter "online"
makes a boot-time sync retry forever offline (the app pegs a core on the splash and never reaches the
title). The two high-level helpers are reached only from the login/New-Account path, so patching them
leaves boot seeing the real (offline) state while letting New Account through.

Timing matters too: the shim's in-memory config patch (`disablegm=0` → local http) must land before the
client's first reachability-gated sync, or that sync hits the online URL, fails offline, and spins. The
shim's patch loop runs up to ~5 min and re-resolves `PackageInfo.Instance` every 30 ms, so it lands even
on a slow cold boot.

Asset delivery (the ~1.7 GB OBB + the extra bundles) is the other non-root consideration: the OBB goes
to the public `Android/obb/<pkg>/` (writable over `adb push` without root), and the bundles go to the
app's external files dir. `sync_device_assets.ps1` uses `su` for a rooted emulator; a pure-non-root
flow pushes to those public paths instead. See [01-PLAY.md](01-PLAY.md).
