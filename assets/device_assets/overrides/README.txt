Files pushed into the game's files folder (/sdcard/Android/data/com.biligameglobal.girlcafegungp/files/) on top of the OBB.
assets\sync_phone.ps1 installs them (non-root phones).

- lang.u             OBB en_US language table with the 16,186 entries the official English build left in Chinese
                     (stories, events, UI, items, skills) translated to English.
- lua.u              OBB lua.u with: UI_Login.Load2DBG randomized (sky or hall title each launch), and 65 hard-coded
                     on-screen Chinese strings translated. Re-encrypted with Game.BundleEncrypt (seed 0x166920ED).
- versioncrc.json    CRC32 of the patched lua.u; the game rejects lua.u if this does not match. Always ship with lua.u.
- ui/ui_login_prefab.u  build-179 title prefab (4-girl Spine off) with English button labels and best-fit text.
- lua.u also: UI_Fight.lua carries the gamepad layer (tools/lua/GamepadFight.lua, injected by tools/patch_lua_gamepad.py; docs/06-CONTROLLER.md).
- lua.u also: web windows use local pages (Notice -> local server /notice.html, server-error text embedded;
  tools/patch_lua_offline_web.py; docs/07-ONLINE-RESOURCES.md).
