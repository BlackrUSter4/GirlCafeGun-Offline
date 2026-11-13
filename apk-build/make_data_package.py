#!/usr/bin/env python3
"""Builds GirlCafeGun-data.gcg: everything the phone needs besides the APK, as one uncompressed tar.

The in-game installer (installer/gcg/offline/Installer.java) unpacks it on first start:
  GCGDATA              first entry, identifies the file (holds the data version)
  obb/<main obb>       -> Android/obb/<pkg>/
  files/...            -> Android/data/<pkg>/files/

Contents of files/: the merged asset table, every bundle in assets/Bundles that the OBB lacks, the videos (Raw/),
voices and music (AutoAudio/), the patched overrides (English text, random title, CRC file, title prefab), and the
uncensor switch l2d/girl111/love.u.

usage: python make_data_package.py [output path]     (default: <project>/GirlCafeGun-data.gcg)
"""
import io
import os
import sys
import tarfile
import time
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "assets")
BUNDLES = os.path.join(ASSETS, "Bundles")
OVERRIDES = os.path.join(ASSETS, "device_assets", "overrides")
DATA_VERSION = "1"


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "GirlCafeGun-data.gcg")
    obbdir = os.path.join(ASSETS, "obb")
    obb = [f for f in os.listdir(obbdir) if f.startswith("main.") and f.endswith(".obb")][0]
    obb_path = os.path.join(obbdir, obb)
    with zipfile.ZipFile(obb_path) as z:
        in_obb = {n[len("assets/"):] for n in z.namelist() if n.startswith("assets/")}

    entries = []  # (archive name, source path or bytes)
    entries.append(("obb/" + obb, obb_path))
    entries.append(("files/version.data", os.path.join(ASSETS, "device_assets", "version.data")))
    override_rel = set()
    for dp, _, fs in os.walk(OVERRIDES):
        for f in fs:
            if f == "README.txt":
                continue
            p = os.path.join(dp, f)
            rel = os.path.relpath(p, OVERRIDES).replace("\\", "/")
            override_rel.add(rel)
            entries.append(("files/" + rel, p))
    for dp, dirs, fs in os.walk(BUNDLES):
        dirs.sort()
        for f in sorted(fs):
            p = os.path.join(dp, f)
            rel = os.path.relpath(p, BUNDLES).replace("\\", "/")
            if rel.endswith(".orig") or rel == "remoteversion.data" or rel in override_rel:
                continue
            is_asset = rel.endswith(".u") or rel.startswith("Raw/") or rel.startswith("AutoAudio/")
            if is_asset and rel not in in_obb:
                entries.append(("files/" + rel, p))
    entries.append(("files/l2d/girl111/love.u", b"love\n"))

    total = sum(os.path.getsize(s) if isinstance(s, str) else len(s) for _, s in entries)
    print(f"{len(entries)} files, {total / 2**30:.2f} GB -> {out}")
    t0 = time.time()
    with tarfile.open(out + ".part", "w", format=tarfile.GNU_FORMAT) as t:
        def add_bytes(name, data):
            ti = tarfile.TarInfo(name)
            ti.size = len(data)
            ti.mtime = int(time.time())
            ti.mode = 0o644
            t.addfile(ti, io.BytesIO(data))
        add_bytes("GCGDATA", f"GCGDATA {DATA_VERSION}\n".encode())
        for i, (name, src) in enumerate(entries, 1):
            if isinstance(src, bytes):
                add_bytes(name, src)
            else:
                ti = t.gettarinfo(src, arcname=name)
                ti.mode, ti.uid, ti.gid, ti.uname, ti.gname = 0o644, 0, 0, "", ""
                with open(src, "rb") as fh:
                    t.addfile(ti, fh)
            if i % 2000 == 0:
                print(f"  {i}/{len(entries)}")
    os.replace(out + ".part", out)
    print(f"done in {time.time() - t0:.0f}s, {os.path.getsize(out) / 2**30:.2f} GB")


if __name__ == "__main__":
    main()
