#!/usr/bin/env python3
"""Builds the self-installing offline APK from the current gcg_offline_fixed.apk (or another base APK).

  1. compiles shim.c -> lib/arm64-v8a/libmain.so and host.c -> lib/arm64-v8a/libsssjhost.so (Android NDK)
  2. compiles installer/gcg/offline/Installer.java -> classes2.dex (javac + d8)
  3. replaces those entries in the base APK, zipaligns and signs with sssj.keystore (same key as before)

usage: python build_offline_apk.py [base.apk] [out.apk] [--publish <dotnet publish dir> [--setting <dir>]]
  With --publish the embedded server (libsssjdata.so) and the runtime .so files are refreshed too, so a
  server change reaches the APK. tools/package.ps1 does this.
Tool paths: C:\\android-build\\{android-ndk-r27d, platforms\\android-35, bt\\android-15, jdk\\*}.
"""
import glob
import os
import shutil
import subprocess
import sys
import tempfile
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
TOOLS = r"C:\android-build"
NDK_BIN = os.path.join(TOOLS, "android-ndk-r27d", "toolchains", "llvm", "prebuilt", "windows-x86_64", "bin")
CC = os.path.join(NDK_BIN, "aarch64-linux-android30-clang.cmd")
ANDROID_JAR = os.path.join(TOOLS, "platforms", "android-35", "android.jar")
BT = os.path.join(TOOLS, "bt", "android-15")
JDK = glob.glob(os.path.join(TOOLS, "jdk", "*"))[0]


def run(cmd, **kw):
    print("  >", " ".join(os.path.basename(c) if i == 0 else c for i, c in enumerate(cmd))[:200])
    env = dict(os.environ, JAVA_HOME=JDK, PATH=os.path.join(JDK, "bin") + os.pathsep + os.environ["PATH"])
    subprocess.run(cmd, check=True, env=env, **kw)


RUNTIME_SO = ["libhostfxr", "libhostpolicy", "libcoreclr", "libSystem.Native", "libSystem.Globalization.Native",
              "libSystem.IO.Compression.Native", "libSystem.Security.Cryptography.Native.OpenSsl"]


def build_data_tar(publish, setting, dest):
    """libsssjdata.so = gzip tar of the whole publish (apphost, .so, .dll, .json, .a; no .pdb) + setting/."""
    import fnmatch
    import gzip
    import tarfile
    names = sorted(n for n in os.listdir(publish)
                   if n == "SssjServer.App" or any(fnmatch.fnmatch(n, p) for p in ("*.so", "*.dll", "*.json", "*.a")))
    if "SssjServer.App.dll" not in names or "libmonosgen-2.0.a" not in names:
        raise SystemExit("publish dir incomplete: " + publish)

    def ti_for(name, path):
        ti = tarfile.TarInfo(name)
        ti.size = os.path.getsize(path)
        ti.mtime = 0
        ti.mode = 0o755 if name in ("./SssjServer.App",) or name.endswith(".so") else 0o644
        return ti
    with open(dest, "wb") as raw, gzip.GzipFile(fileobj=raw, mode="wb", mtime=0, compresslevel=9) as gz, \
            tarfile.open(fileobj=gz, mode="w", format=tarfile.GNU_FORMAT) as t:
        for n in names:
            p = os.path.join(publish, n)
            with open(p, "rb") as fh:
                t.addfile(ti_for("./" + n, p), fh)
        for dp, dirs, fs in os.walk(setting):
            dirs.sort()
            for f in sorted(fs):
                p = os.path.join(dp, f)
                rel = os.path.relpath(p, setting).replace("\\", "/")
                with open(p, "rb") as fh:
                    t.addfile(ti_for("./setting/" + rel, p), fh)
    print("data tar", dest, os.path.getsize(dest), "bytes,", len(names), "publish files")


def main():
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("base", nargs="?", default=os.path.join(ROOT, "gcg_offline_fixed.apk"))
    ap.add_argument("out", nargs="?", default=os.path.join(ROOT, "GirlCafeGun.apk"))
    ap.add_argument("--publish", help="dotnet publish dir: re-bake libsssjdata.so and the runtime .so from it")
    ap.add_argument("--setting", default=os.path.join(ROOT, "server", "setting"))
    a = ap.parse_args()
    base, out = a.base, a.out
    work = tempfile.mkdtemp(prefix="gcgapk_")
    try:
        libmain = os.path.join(work, "libmain.so")
        host = os.path.join(work, "libsssjhost.so")
        run([CC, "-shared", "-fPIC", "-O2", "-o", libmain, os.path.join(HERE, "shim.c"), "-llog", "-landroid"])
        run([CC, "-fPIE", "-pie", "-O2", "-o", host, os.path.join(HERE, "host.c"), "-llog", "-ldl"])

        classes = os.path.join(work, "classes"); os.makedirs(classes)
        srcs = glob.glob(os.path.join(HERE, "installer", "**", "*.java"), recursive=True)
        # --release 8 gives java.* (incl. LambdaMetafactory, which android.jar lacks); d8 desugars the lambdas
        run([os.path.join(JDK, "bin", "javac.exe"), "--release", "8", "-nowarn",
             "-cp", ANDROID_JAR, "-d", classes] + srcs)
        dexdir = os.path.join(work, "dex"); os.makedirs(dexdir)
        cls = glob.glob(os.path.join(classes, "**", "*.class"), recursive=True)
        run([os.path.join(BT, "d8.bat"), "--release", "--min-api", "21", "--lib", ANDROID_JAR,
             "--output", dexdir] + cls)
        dex = os.path.join(dexdir, "classes.dex")

        repl = {"lib/arm64-v8a/libmain.so": libmain, "lib/arm64-v8a/libsssjhost.so": host}
        if a.publish:
            data = os.path.join(work, "libsssjdata.so")
            build_data_tar(a.publish, a.setting, data)
            repl["lib/arm64-v8a/libsssjdata.so"] = data
            for so in RUNTIME_SO:
                repl["lib/arm64-v8a/%s.so" % so] = os.path.join(a.publish, so + ".so")
        unsigned = os.path.join(work, "unsigned.apk")
        with zipfile.ZipFile(base) as zi, zipfile.ZipFile(unsigned, "w") as zo:
            names = zi.namelist()
            dexname = "classes2.dex"
            while dexname in names and dexname != "classes.dex":
                # keep an existing classes2.dex of the base unless it is our installer
                with zi.open(dexname) as fh:
                    if b"gcg/offline/Installer" in fh.read():
                        break
                dexname = "classes%d.dex" % (int(dexname[7:-4]) + 1)
            repl[dexname] = dex
            for it in zi.infolist():
                n = it.filename
                if n.startswith("META-INF/") and (n.endswith((".SF", ".RSA", ".DSA", ".EC")) or n == "META-INF/MANIFEST.MF"):
                    continue
                data = open(repl.pop(n), "rb").read() if n in repl else zi.read(it)
                ni = zipfile.ZipInfo(n, date_time=it.date_time)
                ni.compress_type, ni.external_attr = it.compress_type, it.external_attr
                zo.writestr(ni, data)
            for n, p in repl.items():   # new entries (classes2.dex)
                ni = zipfile.ZipInfo(n, date_time=(2026, 1, 1, 0, 0, 0))
                ni.compress_type = zipfile.ZIP_DEFLATED
                zo.writestr(ni, open(p, "rb").read())
        aligned = os.path.join(work, "aligned.apk")
        run([os.path.join(BT, "zipalign.exe"), "-f", "-p", "4", unsigned, aligned])
        ks = os.path.join(HERE, "sssj.keystore")
        run([os.path.join(BT, "apksigner.bat"), "sign", "--ks", ks, "--ks-pass", "pass:sssjsssj", "--ks-key-alias", "sssj",
             "--key-pass", "pass:sssjsssj", "--out", out, aligned])
        run([os.path.join(BT, "apksigner.bat"), "verify", out])
        print("built", out, os.path.getsize(out))
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    main()
