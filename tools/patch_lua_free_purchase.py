#!/usr/bin/env python3
"""Patches the client Lua bundle so the offline server can grant store packs without a payment SDK.

The server (IbShopFeature) answers a purchase with Error=0 and Free=1 plus tbItem (the granted items).
Stock IBLogic:ResponseTradeNo then still calls XGCharge for real-money items. This patch adds an early branch:
when tbParam.Free == 1 it shows the reward window and refreshes the shop, and never touches the SDK.

  usage: python -I tools/patch_lua_free_purchase.py [lua.u in] [lua.u out] [versioncrc.json]
  default: assets/device_assets/overrides/lua.u (in place) + its versioncrc.json
lua.u is swap-encrypted (Game.BundleEncrypt, seed 0x166920ED; the routine is its own inverse), CRC32 is taken over the
encrypted bytes. Idempotent: running it twice changes nothing. See docs/05-PHONE-PORT-2026-11-11.md section 3.1.
"""
import json, os, sys, zlib
import UnityPy

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OVR = os.path.join(ROOT, "assets", "device_assets", "overrides")
SEED = 0x166920ED

ANCHOR = b"\tif tbParam.Type == 1 then\r\n\t\tlocal tbIBItem = self:GetIBShopInfo(tbParam.Id);"
BRANCH = (
    b"\t-- offline build: the server already granted the pack (Free == 1), do not call the payment SDK\r\n"
    b"\tif tbParam.Free == 1 then\r\n"
    b"\t\tlocal ui = UI.GetClass('UI_Charge');\r\n"
    b"\t\tUI.OpenAsChild(\"UI_RewardNormal\", tbParam.tbItem or {});\r\n"
    b"\t\tif ui and ui.__bOpen then ui:RefreshShop(tbParam.Id); end\r\n"
    b"\t\treturn\r\n"
    b"\tend\r\n\r\n"
)


def swap(data):
    d = bytearray(data)
    for i in range(2, len(d), 4):
        j = i - 1
        if SEED % j < j // 3:
            d[j], d[i] = d[i], d[j]
    return bytes(d)


def main():
    src = sys.argv[1] if len(sys.argv) > 1 else os.path.join(OVR, "lua.u")
    dst = sys.argv[2] if len(sys.argv) > 2 else src
    crcfile = sys.argv[3] if len(sys.argv) > 3 else os.path.join(OVR, "versioncrc.json")
    env = UnityPy.load(swap(open(src, "rb").read()))
    done = False
    for o in env.objects:
        if o.type.name != "TextAsset":
            continue
        a = o.read()
        if a.m_Name != "IBLogic.lua":
            continue
        s = a.m_Script
        b = s.encode("utf-8", "surrogateescape") if isinstance(s, str) else bytes(s)
        if b"tbParam.Free == 1" in b:
            print("already patched"); return
        if b.count(ANCHOR) != 1:
            sys.exit("anchor not found exactly once in IBLogic.lua: %d" % b.count(ANCHOR))
        b = b.replace(ANCHOR, BRANCH + ANCHOR)
        a.m_Script = b.decode("utf-8", "surrogateescape")
        a.save()
        done = True
    if not done:
        sys.exit("IBLogic.lua not found in the bundle")
    out = swap(env.file.save(packer="original"))
    open(dst, "wb").write(out)
    crc = zlib.crc32(out)
    if os.path.exists(crcfile):
        j = json.load(open(crcfile, encoding="utf-8-sig"))
        j["CRC"]["lua.u"] = str(crc)
        json.dump(j, open(crcfile, "w", encoding="utf-8"), separators=(",", ":"))
    print("patched", dst, len(out), "bytes, CRC32", crc)


if __name__ == "__main__":
    main()
