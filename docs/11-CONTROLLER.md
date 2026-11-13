# 11 — Gamepad support (gcg2#18, started 2026-11-11)

Goal: play the shooting stages with a Bluetooth gamepad connected to the phone. The game reads the controller
itself. No touch overlay, no key-to-touch mapper.

Status: **built and tested offline (mock runtime); not yet tested on a phone with a real controller.**

## 1. What the client already has (evidence)

Found with an own il2cpp v24 metadata dumper and a BL call-site scan of `libil2cpp.so` (both checked against the
known RVAs `Game.Utils.IsNetworkOk` 0x14f48dc and `Game.BundleEncrypt.Decrypt` 0x1756d94).

- Unity 2018.4.36f1, legacy Input. Android delivers gamepad events to it natively.
- The InputManager (`assets/bin/Data/data.unity3d`) already has gamepad axes: `Horizontal`/`Vertical` (joystick
  axis 1/2), `HandleFire` (3rd axis), `RightHorizontal`/`RightVertical` (4th/5th), `Dodge` (button 4),
  `Skill` (button 3), `WeaponSkill` (button 5), `FriendFight` (button 7). The developers planned pad support.
- `Game.Handle` (looks for a pad in `Input.GetJoystickNames`) and `Game.Keyboard` are created by
  `Game.UI_Fight.Start`, but their release bodies are empty or never called. `Game.Keyboard.UpdateInput`
  (0x16bc374) still holds the developers' PC/keyboard fight controls. It shows the calls that drive a fight:

| Action | Call (from `Game.Keyboard`) | Keys in the dev build |
|---|---|---|
| Move | `Game.FightJoystick.SetMoveOffset(Vector2 dir)` (static, normalized; zero = stop) | `GetAxisRaw` Horizontal/Vertical |
| Fire | `GameMode.instance.fightCamera:OnFight(Vector2.zero)` / `:OnFightStop()` | J, Keypad0 |
| Skill 1/2/3 | `Game.UI_FightProxy.OnUseSkillByIdx(0/1/2, null)` | U/I/O, Keypad3/1/2 |
| Dodge (move skill) | `Game.UI_FightProxy.OnUseSkillByIdx(3, null)` | Space |

- Lua is xLua with full `CS.*` access (`Preload.lua`: `Game = CS.Game`). `UI_Fight` is the shooting-stage HUD.

## 2. Design

Two small patches. No C# or native code change.

1. **`apk-build/patch_inputmanager.py`** (runs inside `build_offline_apk.py`): adds raw axes `gcgpad_a1` ..
   `gcgpad_a28` (every joystick axis by number) to the InputManager, because Android pads put the right stick,
   triggers and d-pad on different axes per model. Buttons need no entry (`KeyCode.JoystickButton0..19`).
2. **`tools/lua/GamepadFight.lua`**, injected into `UI_Fight.lua` of `lua.u` by **`tools/patch_lua_gamepad.py`**
   (also updates `versioncrc.json`). `OnOpen` starts a per-frame `Game.Timer`; `OnClose` stops it. Each frame it
   reads the pad and makes the calls in the table above. Touch still works: the pad sends "stop" only for an
   action that it started. Every C# call is in `pcall`; a failure turns off only that action and logs
   `[pad] <action> disabled: ...`.

## 3. Default mapping

| Control | Action |
|---|---|
| Left stick, d-pad | Move |
| A, RB, right trigger | Fire (hold) |
| Right stick | Aim + fire (twin-stick; the stick vector goes to `OnFight`) |
| X / Y / B | Skill 1 / 2 / 3 |
| LB | Dodge (the game's own `Dodge` axis is button 4 = LB) |
| Start | Pause menu (same check as the Android back key in `GameManager.ReLogin`) |
| Select | Auto-fight on/off (`UI_Fight.Ins:OnClickBtnAuto`) |
| L3 + R3 | Debug log on/off: every button and axis change goes to logcat (`[pad] ...`) |

Button numbers are the Unity Android standard (A=0 … RB=5, L3=8, R3=9, Start=10, Select=11). Axis defaults
follow the game's InputManager: right stick 4th/5th, right trigger 3rd. D-pad as hat axes 6/7 is `[INFER]`.
Change `P.cfg` in `tools/lua/GamepadFight.lua`, then run the patch again.

## 4. Build and test

```
python -I tools/lua/test_gamepad.py        # mock runtime test of the Lua module (pip install lupa)
python -I tools/patch_lua_gamepad.py       # refresh the module in overrides/lua.u + versioncrc.json
powershell -File tools\package.ps1         # APK (InputManager patch) + data pack (lua.u)
```

The phone needs both parts: the new APK (axes) and the new `lua.u` + `versioncrc.json` in `files/`
(`assets/sync_phone.ps1`, or the data package). Without the new APK the left stick, buttons and the stock
axes still work; the extra axes read as 0.

Phone test plan:
1. Pair the pad, start a story stage, `adb logcat -s Unity | findstr "\[pad\]"` → expect `[pad] started`.
2. Press L3+R3, move every stick, trigger and the d-pad; note the axis numbers; fix `P.cfg` if needed.
3. Check: move, stop, hold fire, twin-stick aim (does `OnFight(dir)` aim, or only auto-aim?), skills, dodge,
   pause, auto-fight, and touch controls still work with the pad connected.

## 5. Open questions (need the phone)

- Is `Game.Timer.Add(0.01, 0.01, fn)` per frame and repeating? (Lua code uses `Add(0.1, 0.1, fn)` as a repeat.)
- Does `OnFight(dir)` aim in `dir`, or is the vector ignored (auto-aim)? If ignored, right-stick aim needs another call.
- Girl switching and weapon skill (`WeaponSkill` = RB in the game's table) are not mapped yet.
- Menus (story, results, rewards) need touch. UI navigation with the pad is a separate card.
- Other fight HUDs (`UI_BattleFight`, `UI_FightPvP`) are not patched; main target is the story/stage HUD.
