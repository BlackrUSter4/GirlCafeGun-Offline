#!/usr/bin/env python3
"""Adds raw gamepad axes to the client's Unity InputManager (assets/bin/Data/data.unity3d in the APK).

Unity 2018.4 legacy Input can only read a joystick axis through a named InputManager axis. The stock table has
Horizontal/Vertical (axis 1/2), Fire2/HandleFire (axis 3), RightHorizontal (axis 4), RightVertical (axis 5).
Android controllers put the right stick and the triggers on different axes per model (Z/RZ, RX/RY, LTRIGGER/RTRIGGER,
BRAKE/GAS, HAT_X/HAT_Y), so the Lua controller layer (tools/patch_lua_gamepad.py) needs every axis by number.
This script appends 'gcgpad_a1' .. 'gcgpad_a28' (type 2 = joystick axis, all joysticks, raw, no dead zone).
Buttons need nothing: Input.GetKey(KeyCode.JoystickButton0..19) works without an InputManager entry.

  usage: python -I apk-build/patch_inputmanager.py <data.unity3d in> <data.unity3d out>
Idempotent. See docs/11-CONTROLLER.md.
"""
import sys
import UnityPy

PREFIX = "gcgpad_a"
NAXES = 28   # Unity 2018 supports joystick axes 1..28


def main(src, dst):
    env = UnityPy.load(src)
    done = False
    for o in env.objects:
        if o.type.name != "InputManager":
            continue
        t = o.read_typetree()
        axes = t["m_Axes"]
        have = {a["m_Name"] for a in axes}
        tmpl = next(a for a in axes if a["m_Name"] == "RightHorizontal")
        added = 0
        for i in range(1, NAXES + 1):
            name = PREFIX + str(i)
            if name in have:
                continue
            a = dict(tmpl)
            a.update(m_Name=name, descriptiveName="", descriptiveNegativeName="", negativeButton="", positiveButton="",
                     altNegativeButton="", altPositiveButton="", gravity=0.0, dead=0.0, sensitivity=1.0, snap=False,
                     invert=False, type=2, axis=i - 1, joyNum=0)
            axes.append(a)
            added += 1
        o.save_typetree(t)
        print("InputManager: %d axes added, %d total" % (added, len(axes)))
        done = True
    if not done:
        sys.exit("InputManager not found in " + src)
    open(dst, "wb").write(env.file.save(packer="original"))
    # check: reload and read the axes back
    env2 = UnityPy.load(dst)
    for o in env2.objects:
        if o.type.name == "InputManager":
            names = [a["m_Name"] for a in o.read_typetree()["m_Axes"]]
            missing = [PREFIX + str(i) for i in range(1, NAXES + 1) if PREFIX + str(i) not in names]
            if missing:
                sys.exit("check failed, missing: %s" % missing)
            print("check OK:", dst, len(names), "axes")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2])
