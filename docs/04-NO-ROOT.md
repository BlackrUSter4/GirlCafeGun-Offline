# 04 — Installing the bundles & assets WITHOUT root

The normal installer (`INSTALL.bat`) assumes a **rooted** emulator (MuMu Player with Root on),
because pushing the content bundles into another app's `Android/data` folder needs root on most
setups. This page is for a **non-rooted** device/emulator.

Three pieces have to get onto the device. Only one of them is actually hard without root:

| Piece | Goes to | Needs root? |
|-------|---------|-------------|
| The APK | installed normally | **No** — `adb install gcg_offline.apk` |
| The OBB (~1.7 GB) | `/sdcard/Android/obb/<pkg>/` | **No** — `adb push` works here |
| The content bundles (~4.5 GB) | `/sdcard/Android/data/<pkg>/files/` | **This is the hard part** |

`Android/obb/` is a special public location Android lets you write over `adb`. `Android/data/`
is **not** — since Android 11 (scoped storage) a plain `adb push` into another app's
`Android/data` is silently rejected (the file is created, then removed). That's why you need one
of the methods below for the bundles.

Package name throughout: **`com.biligameglobal.girlcafegungp`**.

---

## Step 1 — APK + OBB (no root, always works)

```bash
D=YOUR_DEVICE            # e.g. a phone serial, or 127.0.0.1:16448 for an emulator
P=com.biligameglobal.girlcafegungp

adb -s $D install gcg_offline.apk

# OBB — adb push into Android/obb works without root:
adb -s $D shell mkdir -p /sdcard/Android/obb/$P
adb -s $D push assets/obb/main.10009.$P.obb /sdcard/Android/obb/$P/main.10009.$P.obb
```

Launch once so the app creates its own `Android/data/<pkg>/files` folder, then force-stop it:

```bash
adb -s $D shell monkey -p $P 1 ; sleep 8 ; adb -s $D shell am force-stop $P
```

Now do the bundles with **one** of the methods below.

---

## Step 2 — the bundles (pick one method)

### Method A — Shizuku  ·  recommended, no root, no rebuild

[Shizuku](https://shizuku.rikka.app/) gives apps ADB-level access **without root**. A Shizuku-aware
file manager can then write into `Android/data`.

1. Install **Shizuku** and a file manager that supports it — e.g. **MiXplorer** or **Material
   Files** (both can use Shizuku to reach `Android/data`).
2. Start Shizuku:
   - **Wireless debugging** (Android 11+, no PC needed after setup): follow Shizuku's in-app
     "Start via Wireless debugging" wizard, **or**
   - **From a PC once per boot:**
     ```bash
     adb -s $D shell sh /sdcard/Android/data/moe.shizuku.privileged.api/start.sh
     ```
3. In the file manager, grant it Shizuku access, then **copy the entire contents of the pack's
   `assets/Bundles/` folder into**:
   ```
   Android/data/com.biligameglobal.girlcafegungp/files/
   ```
   Keep the subfolder layout (`p_other/`, `o_big/`, `2danimation/`, …). Also copy
   `assets/device_assets/version.data` into that same `files/` folder, replacing the one there.

This is the most reliable route on Android 11–14.

### Method B — `run-as` (advanced, needs a **debuggable** build)  ·  no root

`adb shell run-as <pkg>` lets you act as the app — but **only if the APK is debuggable**. The build
here produces a normal release APK, so this path requires a one-off tweak: set
`android:debuggable="true"` on `<application>` in the APK's `AndroidManifest.xml` and re-sign
(easiest with `apktool d` / `apktool b`, which this project otherwise avoids). That's extra work and
only worth it if you can't use Shizuku.

With a debuggable build, stream the bundles straight into the app's storage — no root, no temp file:

```bash
D=YOUR_DEVICE ; P=com.biligameglobal.girlcafegungp
tar -C assets/Bundles -cf - . | \
  adb -s $D exec-in run-as $P sh -c 'cd /sdcard/Android/data/'$P'/files && tar -xf -'
adb -s $D exec-in run-as $P sh -c 'cat > /sdcard/Android/data/'$P'/files/version.data' < assets/device_assets/version.data
```

> `exec-in` needs a recent `adb`; on older `adb`, push to `/data/local/tmp` first, then
> `run-as` to copy. `run-as` external-dir access also varies by OEM/Android version — if it's
> rejected, use **Method A (Shizuku)**, which is the reliable route.

### Method C — file manager / MTP by hand  ·  last resort

On **Android 11–12** some file managers (and Windows Explorer over MTP) can still browse into
`Android/data`. Copy `assets/Bundles/` (whole tree) and `version.data` into
`Android/data/com.biligameglobal.girlcafegungp/files/`. On **Android 13+** this is mostly blocked —
use Shizuku (Method A).

---

## Step 3 — launch

```bash
adb -s $D shell am start -n $P/com.crasheye.client.api.unity3d.MainActivity
```

First launch: Terms → **Agree to all & start**; title shows **Local** → **New Account** → play.

## Checking it worked

The bundles are in the right place if this lists files (run via your chosen method's shell, or a
file manager):

```
/sdcard/Android/data/com.biligameglobal.girlcafegungp/files/p_other/scene.u
/sdcard/Android/data/com.biligameglobal.girlcafegungp/files/first_shader.u
```

If the game boots to a black screen or logs `Assets.LoadBundle missing bundle : <name>`, some
bundles didn't copy — redo Step 2 and make sure the **whole** `Bundles/` tree landed under
`files/` with its subfolders intact.

## Why rooted emulators are easier

With root (MuMu Player, Root ON) the installer just `su`-copies everything in one shot — no Shizuku,
no file manager, no rebuild. If you only need to *play* and don't care about using your phone, a
rooted emulator is the least-effort path. Root is **not** needed to *run* the game (the server runs
from the app's native-library dir) — only to *deliver the bundles* the easy way.
