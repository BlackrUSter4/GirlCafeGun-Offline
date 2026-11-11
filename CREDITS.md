# Credits, attribution & intellectual property

This project would not exist without the work below. Please read this before
using, forking, or redistributing anything here.

## The game — Girl Cafe Gun (咖啡枪物语 / GCG)

**All rights to Girl Cafe Gun belong to its creators and publishers.** This
includes every piece of game art, music, voice, text, the game client, the
character and story IP, and the trademarks.

- Developer / IP holder: **Seasun Games (珠海西山居 / Seasun, a Kingsoft company)**.
- Global (EN) publishing has involved **bilibili** and **Marvelous**-related
  storefronts; see the in-game licenses and the official app listings.

This project is an **unofficial, non-commercial, offline/preservation** effort.
It is **not** affiliated with, endorsed by, or connected to Seasun, Kingsoft,
bilibili, Marvelous, or any official party. **No game assets are distributed in
this repository.** The build runs against game files that **you must already own
and provide yourself** (from your own installation of the official app). If you
are a rights holder and want something changed or removed, please open an issue.

## The server — `sssj-new`

The embedded game server is a copy of the open-source **`sssj-new`** server
reimplementation.

- Upstream project: **`sssj-new` by SteveS233** (a Chinese-language reference
  implementation of the GCG server). This is where the server code came from.
- License: **MIT**, Copyright (c) 2025 **luoxiadesu** (see `server/LICENSE`,
  reproduced verbatim and kept intact per the MIT terms).

What we used from it: the full server source (`server/src/`, projects App / Core
/ Game / Protocol) and the config-table loader. What we changed on top (all under
the server's MIT license): a settle-validation fix in `ChapterHandler`, and two
Android-runtime fixes required to run it natively on a phone — the login-gate
3DES cipher moved to the managed **BouncyCastle** implementation, and random-key
generation moved to `Random.Shared` (both avoid an OpenSSL-init crash on Android).

## This project's original work (MIT — see `LICENSE`)

- The offline-APK build tooling: `apk-build/shim.c`, `host.c`, `repack.py`,
  `patch_il2cpp.py`.
- The installer / build / packaging scripts in `scripts/`.
- The documentation in `docs/`, this file, and the project page.

## Third-party components pulled at build time

- **.NET 10** runtime & SDK — © Microsoft, MIT.
- **BouncyCastle.Cryptography** — © The Legion of the Bouncy Castle, MIT.
- **Android NDK / build-tools** (clang, zipalign, apksigner) — © Google / AOSP.

## Responsible use

- This is for **personal, offline, preservation and study** use with game files
  you already own. Do not use it to access official online services, to gain any
  advantage on official servers, or for anything commercial.
- **Scan everything you download before running it** (e.g. VirusTotal, an AV, or
  your own analysis). Prebuilt binaries are provided as-is with no warranty.
- Don't redistribute the game's copyrighted assets. Share the *code* (this repo);
  let people bring their own game files.
