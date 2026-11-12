#!/usr/bin/env python3
"""Patches the client Lua bundle so in-game web windows load local pages instead of dead publisher pages.

Why: envir.json in the OBB (environment "en") still names Bilibili pages for the web windows:
  notice                                  https://game-intl.biligame.net/girlcafegun/notice/h5/
  help, word, errUrlServerlist, errUrlGate,
  errUrlServer, chargeCallback            https://game-intl.biligame.net/girlcafegun/notice/maintenance/h5/
Both answer with an Alibaba OSS "NoSuchKey" XML page, and the game shows that page in its Notice window.
The OBB is 4+ GB, so the fix goes into lua.u (3 MB, already an override in files/).

What it does (docs/07-ONLINE-RESOURCES.md):
  1. Lib.lua: adds OfflineWebUrl(url) and OfflineErrorHtml().
     OfflineWebUrl maps the two dead page addresses to the local server: scheme://host:port of
     Game.Dir.GetBaseUpdateUrl() (on the phone that base is http://127.0.0.1:9080/10009/) + "/notice.html" and
     "/web/maintenance/index.html", served by BootstrapHttpServer / WebAssets.cs. Other URLs pass unchanged.
  2. Every `uniwebview:Load(X)` in UI_Notice, UI_User, UI_OtherTip, UI_RikiComic becomes
     `uniwebview:Load(OfflineWebUrl(X))`.
  3. UI_ServerError (opens when the server does not answer) shows OfflineErrorHtml() through
     UniWebView.LoadHTMLString, so it needs no server at all. If that call fails it falls back to the mapped URL.

  usage: python -I tools/patch_lua_offline_web.py [lua.u in] [lua.u out] [versioncrc.json]
  default: assets/device_assets/overrides/lua.u (in place) + its versioncrc.json
lua.u is swap-encrypted (Game.BundleEncrypt, seed 0x166920ED; the routine is its own inverse), CRC32 is taken over the
encrypted bytes. Idempotent: running it twice changes nothing. See docs/05-PHONE-PORT-2026-11-11.md section 3.1.
"""
import json, os, re, sys, zlib
import UnityPy

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OVR = os.path.join(ROOT, "assets", "device_assets", "overrides")
SEED = 0x166920ED
MARK = b"-- offline build: local web pages (tools/patch_lua_offline_web.py)"

# Lib.lua: inserted after LoadLocalUrl. Plain ASCII. The HTML has no '#' and no '%' on purpose: some WebView
# load paths treat them as URL syntax.
LIB_ANCHOR = re.compile(rb"function LoadLocalUrl\(key\)\r?\n\treturn Game\.LocalUrl\.Get\(key\);\r?\nend\r?\n")
LIB_BLOCK = b"""
-- offline build: local web pages (tools/patch_lua_offline_web.py)
-- The pages below were on the publisher's web servers. The local game server now has them under web/.
local tbOfflineWeb = {
	{ "https://game-intl.biligame.net/girlcafegun/notice/maintenance/h5/", "web/maintenance/index.html" },
	-- notice.html, not web/notice/index.html: an APK built before the web/ folder existed also answers it
	{ "https://game-intl.biligame.net/girlcafegun/notice/h5/", "notice.html" },
}
function OfflineWebUrl(url)
	if type(url) ~= "string" then return url end
	local ok, base = pcall(function() return Game.Dir.GetBaseUpdateUrl() end)
	if not ok or base == nil or base == "" then base = "http://127.0.0.1:9080/" end
	-- keep scheme://host:port only: on the phone the update base is http://127.0.0.1:9080/10009/ (version code added)
	base = (string.match(base, "^(%a[%w+.-]*://[^/]+)") or "http://127.0.0.1:9080") .. "/"
	for _, tb in ipairs(tbOfflineWeb) do
		if string.sub(url, 1, string.len(tb[1])) == tb[1] then
			return base .. tb[2] .. string.sub(url, string.len(tb[1]) + 1)
		end
	end
	return url
end
-- Page for UI_ServerError. It opens when the local server does not answer, so the text is here and not on the server.
function OfflineErrorHtml()
	return [==[<!DOCTYPE html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1,user-scalable=no">
<style>
body{margin:0;padding:16px;background:rgb(243,245,248);color:rgb(43,48,56);font:15px/1.7 Roboto,Arial,sans-serif}
.box{max-width:560px;margin:0 auto;background:white;border-radius:4px;padding:20px 24px}
h1{font-size:19px;margin:0 0 10px;color:rgb(11,123,176)}
ol{margin:8px 0 0;padding-left:22px}
.tip{color:rgb(95,107,122);font-size:12px;margin:14px 0 0}
</style></head><body><div class="box">
<h1>The game server did not answer</h1>
<p>This is the offline build. The game server runs inside the app on this device. It needs no internet.</p>
<ol>
<li>Close the game fully (remove it from the recent apps list).</li>
<li>Open the game again and wait at the title screen for a few seconds.</li>
<li>If the error stays, restart the device.</li>
</ol>
<p class="tip">Your save data stays on the device. This error does not delete it.</p>
</div></body></html>]==]
end
"""

# `    self.uniwebview:Load(<expr>)` on one line -> wrap <expr>.
LOAD_LINE = re.compile(rb"^(?P<head>[ \t]*self\.uniwebview:Load\()(?P<arg>.*)(?P<tail>\)[ \t]*;?[ \t]*)$")
WRAP_FILES = {"UI_Notice.lua": 9, "UI_User.lua": 1, "UI_OtherTip.lua": 1, "UI_RikiComic.lua": 1}  # name -> Load lines

ERR_OLD = b'self.uniwebview:Load(string.format("%s?time=%d", url, os.time()))'
ERR_NEW = (b'if not pcall(function() self.uniwebview:LoadHTMLString(OfflineErrorHtml(), "", false) end) then '
           b'self.uniwebview:Load(OfflineWebUrl(string.format("%s?time=%d", url, os.time()))) end')


def swap(data):
    d = bytearray(data)
    for i in range(2, len(d), 4):
        j = i - 1
        if SEED % j < j // 3:
            d[j], d[i] = d[i], d[j]
    return bytes(d)


def patch_lib(b):
    if MARK in b:
        # a block from an earlier version of this tool: replace it when its text differs
        nl = b"\r\n" if b"\r\n" in b else b"\n"
        block = LIB_BLOCK.replace(b"\r\n", b"\n").replace(b"\n", nl)
        start = b.index(nl + MARK)
        end = b.index(b"]==]" + nl + b"end" + nl, start) + len(b"]==]" + nl + b"end" + nl)
        if b[start:end] == block:
            return b, 0
        return b[:start] + block + b[end:], 1
    m = LIB_ANCHOR.search(b)
    if not m or len(LIB_ANCHOR.findall(b)) != 1:
        sys.exit("Lib.lua: LoadLocalUrl anchor not found exactly once")
    nl = b"\r\n" if b"\r\n" in m.group() else b"\n"
    block = LIB_BLOCK.replace(b"\r\n", b"\n").replace(b"\n", nl)
    return b[:m.end()] + block + b[m.end():], 1


def patch_wrap(name, b, expect):
    nl = b"\r\n" if b"\r\n" in b else b"\n"
    lines, changed, seen = b.split(nl), 0, 0
    for i, line in enumerate(lines):
        m = LOAD_LINE.match(line)
        if not m:
            continue
        seen += 1
        if m.group("arg").startswith(b"OfflineWebUrl("):
            continue
        lines[i] = m.group("head") + b"OfflineWebUrl(" + m.group("arg") + b")" + m.group("tail")
        changed += 1
    if seen != expect:
        sys.exit("%s: expected %d uniwebview:Load lines, found %d" % (name, expect, seen))
    return nl.join(lines), changed


def patch_error(b):
    if b"OfflineErrorHtml()" in b:
        return b, 0
    if b.count(ERR_OLD) != 1:
        sys.exit("UI_ServerError.lua: Load line not found exactly once: %d" % b.count(ERR_OLD))
    return b.replace(ERR_OLD, ERR_NEW), 1


def main():
    src = sys.argv[1] if len(sys.argv) > 1 else os.path.join(OVR, "lua.u")
    dst = sys.argv[2] if len(sys.argv) > 2 else src
    crcfile = sys.argv[3] if len(sys.argv) > 3 else os.path.join(OVR, "versioncrc.json")
    env = UnityPy.load(swap(open(src, "rb").read()))
    todo = {"Lib.lua", "UI_ServerError.lua"} | set(WRAP_FILES)
    total = 0
    for o in env.objects:
        if o.type.name != "TextAsset":
            continue
        a = o.read()
        if a.m_Name not in todo:
            continue
        todo.discard(a.m_Name)
        s = a.m_Script
        b = s.encode("utf-8", "surrogateescape") if isinstance(s, str) else bytes(s)
        if a.m_Name == "Lib.lua":
            b, n = patch_lib(b)
        elif a.m_Name == "UI_ServerError.lua":
            b, n = patch_error(b)
        else:
            b, n = patch_wrap(a.m_Name, b, WRAP_FILES[a.m_Name])
        print("%-20s %d change(s)" % (a.m_Name, n))
        if n:
            a.m_Script = b.decode("utf-8", "surrogateescape")
            a.save()
            total += n
    if todo:
        sys.exit("not found in the bundle: %s" % ", ".join(sorted(todo)))
    if not total:
        print("already patched")
        return
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
