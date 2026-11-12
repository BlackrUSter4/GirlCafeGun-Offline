#!/usr/bin/env python3
# Point the game at the LOCAL embedded server, permanently — the real fix for the
# "Connection error. Please check if WiFi is turned on." bug.
#
# The client reads its server list from assets/envir.json inside the OBB:
# mapEnvir["en"].serverlist = the online hosts (l13-prod-us-serverlist-sssj.bilibiligame.net, ...).
# On launch it builds checkupdate/serverlist from that, so on a slow/cold boot — before the shim's
# in-memory config patch lands — it hits the ONLINE host, fails offline, and shows the connection
# error. Rewriting envir.json so the "en" environment points at http://127.0.0.1:9080 removes the
# online call entirely: the client always talks to the embedded server, with no runtime race.
#
# envir.json is STORED (uncompressed) in the OBB zip, so we overwrite it in place and fix the CRC in
# both the local file header and the central directory — no multi-GB re-zip. The entry's byte length
# is preserved (padded with trailing spaces, which JSON ignores), and the serverlist entry COUNT is
# kept (zoneServerNums expects it) with every entry pointed local.
#
# usage: python patch_obb_envir.py <main.*.obb> [host]      (default host: http://127.0.0.1:9080)
import sys, zipfile, zlib, struct, json

def main(obb, host):
    z = zipfile.ZipFile(obb); info = z.getinfo("assets/envir.json"); orig = z.read("assets/envir.json"); z.close()
    size = info.file_size
    d = json.loads(orig)
    en = d["mapEnvir"][d.get("cur", "en")]
    n = max(1, len(en.get("serverlist", [])))
    en["serverlist"] = [host] * n
    en["updateUrl"] = host.rstrip("/") + "/"
    new = json.dumps(d, ensure_ascii=False, indent=2).encode("utf-8")
    if len(new) > size:
        sys.exit(f"edited envir.json ({len(new)}) larger than original ({size}); shorten host")
    new = new + b" " * (size - len(new))
    crc = zlib.crc32(new) & 0xFFFFFFFF
    with open(obb, "r+b") as f:
        base = info.header_offset
        f.seek(base)
        if f.read(4) != b"PK\x03\x04":
            sys.exit("local header not found at header_offset; unexpected OBB layout")
        f.seek(base + 26); nl, el = struct.unpack("<HH", f.read(4))
        f.seek(base + 14); f.write(struct.pack("<I", crc))          # local header CRC
        f.seek(base + 30 + nl + el); f.write(new)                   # file data (STORED)
        blob = open(obb, "rb").read()
        eocd = blob.rfind(b"PK\x05\x06")
        cdoff = struct.unpack("<I", blob[eocd + 16:eocd + 20])[0]
        cdsize = struct.unpack("<I", blob[eocd + 12:eocd + 16])[0]
        p = cdoff; patched = False
        while p < cdoff + cdsize:
            if blob[p:p + 4] != b"PK\x01\x02":
                nx = blob.find(b"PK\x01\x02", p + 1, cdoff + cdsize)
                if nx < 0: break
                p = nx; continue
            cnl = struct.unpack("<H", blob[p + 28:p + 30])[0]
            cel = struct.unpack("<H", blob[p + 30:p + 32])[0]
            ccl = struct.unpack("<H", blob[p + 32:p + 34])[0]
            if blob[p + 46:p + 46 + cnl] == b"assets/envir.json":
                f.seek(p + 16); f.write(struct.pack("<I", crc)); patched = True; break
            p += 46 + cnl + cel + ccl
        if not patched:
            sys.exit("central-directory entry for assets/envir.json not found")
    # verify
    z = zipfile.ZipFile(obb); r = json.loads(z.read("assets/envir.json")); z.close()
    print(f"OK: {d.get('cur','en')}.serverlist -> {r['mapEnvir'][d.get('cur','en')]['serverlist'][0]} x{n}")

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("usage: python patch_obb_envir.py <main.*.obb> [host]"); sys.exit(2)
    main(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else "http://127.0.0.1:9080")
