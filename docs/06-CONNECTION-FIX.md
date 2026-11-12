# 06 — The "Connection error" fix (always talk to the local server)

Some offline builds intermittently show **"Connection error. Please check if WiFi is turned on."** on
launch, or get stuck on the splash. This build fixes that at the source. Here's what was happening and
how it's fixed.

## Why it happened

The client reads its server list from `assets/envir.json` inside the OBB. The `"en"` environment lists
the **official online** serverlist hosts:

```json
"en": { "serverlist": [
  "https://l13-prod-us-serverlist-sssj.bilibiligame.net",
  "https://l11-prod-sgp-serverlist-sssj.bilibiligame.net", ... ] }
```

On launch the client builds its `checkupdate`/`serverlist` request from that list. The offline shim
patches the config in memory to redirect to `127.0.0.1`, but that patch runs in a loop and
`SetServerListUrl()` executes at `PackageInfo.Load` — sometimes **before** the patch lands. On a slow or
cold boot the request therefore went to the **online** host, failed (no internet), and the client showed
the connection error. It was a timing race, so it was intermittent — worse on cold/loaded emulators.

## The fix

Rewrite `envir.json` so the `"en"` environment points at the **local embedded server**
(`http://127.0.0.1:9080`) instead of the online hosts. Now the client builds a local URL from the start —
there is **no online call to lose a race against**. Checkupdate succeeds locally every boot:

```
DownloadVersionList start : 10009.Android.en. url:http://127.0.0.1:9080/checkupdate
DownloadVersionList msg : 0.
```

`envir.json` is STORED (uncompressed) inside the OBB zip, so the patch overwrites that one entry in place
and fixes its CRC — no multi-GB re-zip. The serverlist entry **count** is preserved (the `zoneServerNums`
field expects it), with every entry pointed local; the title's server picker just shows every region at
~1 ms (all local).

This is a **static config edit** — it does not touch `libil2cpp`. It removes the part that actually broke
things: the client **no longer contacts the official server at all** (verified: zero online checkupdate
attempts across boots). That was the reported bug — "it still calls an official server and can fail with
connection error."

### One caveat: the local URL's http-vs-https still rides on the shim

With the host now always `127.0.0.1`, the only thing left to a runtime race is the URL *scheme*. On a
cold boot, before the shim sets `disablegm=0`, the client can upgrade the local URL `http://…` →
`https://…`; the embedded server speaks plain http, so that attempt fails against the *local* server (not
an online one). The shim's `disablegm=0` makes it http, and on a warm boot that lands before the URL is
built. If a very cold boot gets stuck on the splash, **relaunch once** — the shim lands and it connects
http locally. Either way it never reaches for the official server.

## Reproducing it

The shipped OBB in the asset pack is already patched. To redo it on a fresh/stock OBB:

```bash
python apk-build/patch_obb_envir.py assets/obb/main.10009.com.biligameglobal.girlcafegungp.obb
# or point somewhere else:  python apk-build/patch_obb_envir.py <obb> http://127.0.0.1:9080
```

Then install that OBB as usual (the installer pushes it). Verified end-to-end on an arm64 emulator: zero
online checkupdate attempts across boots, local checkupdate succeeds, the title's "Select server" shows
America/Europe/Asia all at 1–2 ms (local), New Account proceeds.
