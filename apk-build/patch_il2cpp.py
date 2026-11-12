#!/usr/bin/env python3
# Patch libil2cpp.so so the game plays in full airplane mode (no network at all).
#
# The offline build already routes everything to 127.0.0.1, but the client gates "New Account" on the
# OS reporting a network: Game.Utils.IsNetworkOk() returns (Application.internetReachability != 0), and
# the New Account Lua refuses with "Connection error. Please check your network." when it's false. We
# force that check (and the login-screen variant) to always return true.
#
# We do NOT patch the underlying get_internetReachability getter: the BOOT sequence also consults
# reachability, and forcing the global getter "online" makes a boot-time sync retry forever offline.
# Patching only the two high-level helpers leaves boot seeing the real (offline) state while letting the
# New Account path through. RVAs are from the Il2CppDumper dump of this exact 1.0.9 libil2cpp.so;
# Il2CppDumper's Offset == file offset here (verified against the on-disk bytes).
#
# The actual "stops calling the official server" fix lives in the OBB config, not here: the client reads
# its serverlist from assets/envir.json, so patch_obb_envir.py points the "en" environment at the local
# server (http://127.0.0.1:9080). That removes the online call and its runtime race. (The in-memory shim
# still sets disablegm=0 so the local URL stays http rather than being upgraded to https.)
#
# usage: python patch_il2cpp.py <libil2cpp.so>   (patches in place)
import sys

# RVA/file-offset -> (name, expected original first 4 bytes, patch bytes)
# patch = `mov w0, #1 ; ret`  (return true)
MOVW0_1_RET = bytes([0x20, 0x00, 0x80, 0x52, 0xC0, 0x03, 0x5F, 0xD6])
PATCHES = {
    0x14F48DC: ("Game.Utils.IsNetworkOk",            b"\xf3\x0f\x1e\xf8", MOVW0_1_RET),
    0x1BEBDA4: ("Game.Login.IsNetworkReachability",  b"\xf3\x0f\x1e\xf8", MOVW0_1_RET),
}

def main(path):
    with open(path, "r+b") as f:
        for off, (name, want_head, patch) in PATCHES.items():
            f.seek(off); head = f.read(4)
            if head == patch[:4]:
                print(f"  {name} @0x{off:X}: already patched"); continue
            if head != want_head:
                print(f"  {name} @0x{off:X}: UNEXPECTED bytes {head.hex()} (want {want_head.hex()}) - wrong binary?")
                sys.exit(1)
            f.seek(off); f.write(patch)
            print(f"  {name} @0x{off:X}: patched ({len(patch)} bytes)")
    print("done")

if __name__ == "__main__":
    if len(sys.argv) != 2:
        print("usage: python patch_il2cpp.py <libil2cpp.so>"); sys.exit(2)
    main(sys.argv[1])
