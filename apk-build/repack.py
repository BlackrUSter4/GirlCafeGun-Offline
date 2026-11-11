import zipfile, os, sys

src = "orig.apk"
out = "gcg_offline_unsigned.apk"
newlibs = "lib/arm64-v8a"
replace = {"lib/arm64-v8a/libmain.so"}

def is_sig(name):
    b = name.upper()
    return b.startswith("META-INF/") and (b.endswith(".SF") or b.endswith(".RSA")
        or b.endswith(".DSA") or b.endswith(".EC") or b.endswith("MANIFEST.MF"))

added = [f"lib/arm64-v8a/{f}" for f in sorted(os.listdir(newlibs))]
added_set = set(added)

with zipfile.ZipFile(src) as zin, zipfile.ZipFile(out, "w", allowZip64=True) as zout:
    for item in zin.infolist():
        if item.filename in replace or item.filename in added_set: continue
        if is_sig(item.filename): continue
        data = zin.read(item.filename)
        zi = zipfile.ZipInfo(item.filename, date_time=item.date_time)
        zi.compress_type = item.compress_type
        zi.external_attr = item.external_attr
        zi.create_system = item.create_system
        zout.writestr(zi, data)
    for rel in added:
        with open(rel, "rb") as fh:
            data = fh.read()
        # data tar is already gzip-compressed -> store; others deflate
        ct = zipfile.ZIP_STORED if rel.endswith("libsssjdata.so") else zipfile.ZIP_DEFLATED
        zi = zipfile.ZipInfo(rel)
        zi.compress_type = ct
        zi.external_attr = (0o644 << 16)
        zout.writestr(zi, data)

print("wrote", out, os.path.getsize(out))
print("added:", len(added), "entries")
