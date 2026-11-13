#!/usr/bin/env python3
"""Injects the gamepad layer (tools/lua/GamepadFight.lua) into UI_Fight.lua inside the client Lua bundle.

UI_Fight is the HUD of the shooting stages. The patch adds the module at the top of UI_Fight.lua (between marker
lines), GamepadFight.Start() at the end of OnOpen and GamepadFight.Stop() in OnClose. Re-running replaces the module
with the current tools/lua/GamepadFight.lua, so edit that file and run this again.

  usage: python -I tools/patch_lua_gamepad.py [lua.u in] [lua.u out] [versioncrc.json]
  default: assets/device_assets/overrides/lua.u (in place) + its versioncrc.json
lua.u is swap-encrypted (see tools/patch_lua_free_purchase.py, docs/05 section 3.1); the CRC32 of the encrypted file
goes into versioncrc.json, or the game rejects lua.u. See docs/11-CONTROLLER.md.
"""
import json, os, re, sys, zlib
import UnityPy

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OVR = os.path.join(ROOT, "assets", "device_assets", "overrides")
MODULE = os.path.join(ROOT, "tools", "lua", "GamepadFight.lua")
SEED = 0x166920ED
BEGIN, END = b"-- >>> gcg2#18 gamepad (tools/patch_lua_gamepad.py)", b"-- <<< gcg2#18 gamepad"

REGISTER = re.compile(rb"local tbClass = UI\.Register\('UI_Fight'\);\r?\n")
OPEN = re.compile(rb"( *)Game\.GlobalEvent\.AddLuaEvent\(Game\.eEventName\.OnNotifyUpdateAutoFight, self\.OnUpdateAutoFight\)\r?\n")
CLOSE = re.compile(rb"( *)Game\.GlobalEvent\.RemoveLuaEvent\(Game\.eEventName\.OnNotifyUpdateAutoFight, self\.OnUpdateAutoFight\)\r?\n")
START, STOP = b"GamepadFight.Start() -- gcg2#18", b"GamepadFight.Stop() -- gcg2#18"


def swap(data):
    d = bytearray(data)
    for i in range(2, len(d), 4):
        j = i - 1
        if SEED % j < j // 3:
            d[j], d[i] = d[i], d[j]
    return bytes(d)


def one(rx, b, what):
    m = list(rx.finditer(b))
    if len(m) != 1:
        sys.exit("UI_Fight.lua: %s anchor found %d times (want 1)" % (what, len(m)))
    return m[0]


def patch(b, module):
    nl = b"\r\n" if b"\r\n" in b else b"\n"
    block = BEGIN + nl + module.replace(b"\r\n", b"\n").replace(b"\n", nl).rstrip() + nl + END + nl
    if BEGIN in b:   # refresh the module, keep the hooks
        a = b.index(BEGIN); e = b.index(END, a)
        e = b.index(b"\n", e) + 1
        return b[:a] + block + b[e:], "module refreshed"
    m = one(REGISTER, b, "UI.Register")
    b = b[:m.end()] + block + b[m.end():]
    m = one(OPEN, b, "OnOpen AddLuaEvent")
    b = b[:m.end()] + m.group(1) + START + nl + b[m.end():]
    m = one(CLOSE, b, "OnClose RemoveLuaEvent")
    b = b[:m.end()] + m.group(1) + STOP + nl + b[m.end():]
    return b, "patched"


def main():
    src = sys.argv[1] if len(sys.argv) > 1 else os.path.join(OVR, "lua.u")
    dst = sys.argv[2] if len(sys.argv) > 2 else src
    crcfile = sys.argv[3] if len(sys.argv) > 3 else os.path.join(OVR, "versioncrc.json")
    module = open(MODULE, "rb").read()
    env = UnityPy.load(swap(open(src, "rb").read()))
    status = None
    for o in env.objects:
        if o.type.name != "TextAsset":
            continue
        a = o.read()
        if a.m_Name != "UI_Fight.lua":
            continue
        s = a.m_Script
        b = s.encode("utf-8", "surrogateescape") if isinstance(s, str) else bytes(s)
        nb, status = patch(b, module)
        if nb == b:
            print("UI_Fight.lua: already up to date"); return
        a.m_Script = nb.decode("utf-8", "surrogateescape")
        a.save()
    if status is None:
        sys.exit("UI_Fight.lua not found in the bundle")
    out = swap(env.file.save(packer="original"))
    open(dst, "wb").write(out)
    crc = zlib.crc32(out)
    if os.path.exists(crcfile):
        j = json.load(open(crcfile, encoding="utf-8-sig"))
        j["CRC"]["lua.u"] = str(crc)
        json.dump(j, open(crcfile, "w", encoding="utf-8"), separators=(",", ":"))
    print("UI_Fight.lua:", status, "->", dst, len(out), "bytes, CRC32", crc)


if __name__ == "__main__":
    main()
