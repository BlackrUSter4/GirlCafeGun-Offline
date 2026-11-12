#!/usr/bin/env python3
"""Builds the self-installing offline APK from the current gcg_offline_fixed.apk (or another base APK).

  1. compiles shim.c -> lib/arm64-v8a/libmain.so and host.c -> lib/arm64-v8a/libsssjhost.so (Android NDK)
  2. compiles installer/gcg/offline/Installer.java -> classes2.dex (javac + d8)
  3. replaces those entries in the base APK, zipaligns and signs with sssj.keystore (same key as before)

usage: python build_offline_apk.py [base.apk] [out.apk]
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


def main():
    base = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "gcg_offline_fixed.apk")
    out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(ROOT, "GirlCafeGun.apk")
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
