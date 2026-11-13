# 09 — Uncensor patch (restoring the Live2D interactions)

The global (EN) release of Girl Cafe Gun removed some of the Live2D **touch / flirting
interactions** that the earlier Korean and Chinese builds had. This build restores them. The
installer applies the patch automatically — this page explains what it does and how to do it by hand.

## What's actually censored (and what isn't)

Traced from the client's own `S2CCall.lua` (`SetLive2DEnableLevel`) and `Live2DMgr` (il2cpp):

- The censorship is **not** removed art or a covered-up texture. The interaction **animations are
  still in the EN model** — `girl111/l2d01.u` contains `Mgirl111_douqi / jidong / stand / taolun`,
  the same motions the CN/JP build has.
- It's a **runtime gate**: the *official* server sends a `SetLive2DEnableLevel` message that lowers
  the Live2D "enable level" and sets the harmonize (和谐 / HX) flag, which suppresses the
  interactions on `en_US`.
- The client has a **built-in bypass**: on `en_US`, if the file `l2d/girl111/love.u` exists in the
  game's storage, it **ignores the server's censor message** and keeps the full enable level:

  ```lua
  -- S2CCall.lua : SetLive2DEnableLevel(tb)
  if Game.Localization.Lang == Game.LangType.en_US and tb then
      local path = Game.Dir.storage .. "l2d/girl111/love.u"
      if CS.System.IO.File.Exists(path) and not tb.byserver2 then
          ok = false; Game.Live2DMgr.IsEncrypt = false   -- skip the server's censorship below
      end
  end
  ```

`love.u` is only ever **checked for existence** (it is never loaded as a model), so the file's
*contents* don't matter — **any file, even an empty one, works** as the switch. This matches the
community method documented on the GCG Wiki (<https://wiki.girl.cafe/de-censor/android/>), which has
you create an empty `love.u` at exactly this path.

Two things make this build uncensored:
1. our **offline server sends no censor flag** in the first place (so the enable level stays at the
   client's login default of 3 = full), and
2. the **`love.u` switch** is in place as a belt-and-braces bypass of any censor message.

## Automatic (the installer does this)

`INSTALL.bat` / `install.ps1` applies it after syncing the bundles: it creates an empty
`l2d/girl111/love.u` on the device. Nothing else to do.

To install **without** the uncensor (leave it as the global release):

```
INSTALL.bat -NoUncensor
```

## By hand

The file goes next to the other content bundles, at:

```
/sdcard/Android/data/com.biligameglobal.girlcafegungp/files/l2d/girl111/love.u
```

**Rooted emulator (MuMu):**

```bash
D=127.0.0.1:16448 ; P=com.biligameglobal.girlcafegungp
F=/data/media/0/Android/data/$P/files/l2d/girl111
adb -s $D shell "su 0 sh -c 'mkdir -p $F; : > $F/love.u; chown 2000:2000 $F/love.u; chmod 666 $F/love.u'"
```

**No root:** create the file the same way you delivered the bundles (Shizuku, a file manager, etc. —
see [04-NO-ROOT.md](04-NO-ROOT.md)). Copy any file (e.g. `l2d01.u`) to `l2d/girl111/love.u` inside
`Android/data/<pkg>/files/`. It only has to exist.

## How to see that it worked

After restarting the game, the effect shows on the **Live2D interaction**, but only where there's
something to interact with:
- Interact with a girl you have at least **Lv.10 Intimacy** with — the touch/flirting is freer.
- Or open **Shi Wuxia's figure** (Events page or Encyclopedia) and look for the breast-physics tilt.

A brand-new account (still in the tutorial, 0 intimacy) won't show a visible difference yet — the
switch is in place, but there's no high-intimacy interaction to display.

## Scope / limits

- This restores the **cafe / main-screen Live2D touch & flirting interactions**. It does **not** add
  content that the EN build never shipped at all — some costumes/skins and a few characters are
  region-locked out of EN (`Skin.txt` / `GirlFilter.txt`) and their assets simply aren't present.
- The base character art is the **same** across regions (verified by comparing the EN and CN/JP
  texture atlases), so there is no "nude texture" to swap in — the difference was always the
  interaction gate, not the art.
- If a future client update changes `S2CCall.lua`, re-check that the `love.u` path/logic still holds.
