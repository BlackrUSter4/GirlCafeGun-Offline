#!/usr/bin/env python3
"""Offline test of tools/lua/GamepadFight.lua with mocked Unity/game APIs (needs: pip install lupa).

Checks the calls the module makes for scripted controller states: move, stop, fire, stop, skills (edge), dodge,
trigger fire, twin-stick aim, pause, and that a failing C# call disables only that action.
  usage: python -I tools/lua/test_gamepad.py
"""
import os, sys
from lupa import LuaRuntime

HERE = os.path.dirname(os.path.abspath(__file__))

MOCK = r"""
calls = {}
local function log(s) calls[#calls+1] = s end
pad = { buttons = {}, axes = {}, h = 0, v = 0 }
timers = {}
U3D = {
  KeyCode = { __CastFrom = function(n) return n end },
  Vector2 = function(x, y) return { x = x, y = y } end,
  Input = {
    GetKey = function(k) return pad.buttons[k - 330] == true end,
    GetAxisRaw = function(name)
      if name == "Horizontal" then return pad.h end
      if name == "Vertical" then return pad.v end
      local n = tonumber(name:match("^gcgpad_a(%d+)$"))
      if n then return pad.axes[n] or 0 end
      error("unknown axis " .. name)
    end,
    GetJoystickNames = function() return { Length = 1, [0] = "Mock Pad" } end,
  },
}
local cam = {
  OnFight = function(self, v) log(string.format("fire %.2f %.2f", v.x, v.y)) end,
  OnFightStop = function(self) log("firestop") end,
}
paused = false
Game = {
  GameMode = { instance = { fightCamera = cam, isBegin = true }, IsEnd = false },
  Map = { isOnline = false }, CartoonMgr = { IsPlaying = false },
  FightJoystick = { SetMoveOffset = function(v) log(string.format("move %.2f %.2f", v.x, v.y)) end },
  UI_FightProxy = { OnUseSkillByIdx = function(i, p)
      if failSkill then error("boom") end
      log("skill " .. i) end },
  UI_Fight = { Ins = { OnClickBtnAuto = function(self) log("auto") end } },
  Timer = { Add = function(d, i, fn) timers[#timers+1] = fn; return #timers end,
            Delete = function(id) timers[id] = nil end },
}
UI = { IsOpen = function(n) return n == "UI_MissionPause" and paused end,
       Open = function(n) paused = true; log("open " .. n) end,
       Close = function(n) paused = false; log("close " .. n) end }
function frame() for _, fn in pairs(timers) do fn() end end
function take() local c = calls; calls = {}; return table.concat(c, "|") end
"""

STEPS = [
    # (setup lua, expected calls after one frame)
    ("", ""),
    ("pad.h = 1; pad.v = 0.1", "move 1.00 0.10"),        # normalized
    ("", "move 1.00 0.10"),                               # held: sent every frame (absolute direction)
    ("pad.h = 0.1; pad.v = 0", "move 0.00 0.00"),        # inside dead zone -> one stop
    ("", ""),                                             # no repeated stop (touch keeps control)
    ("pad.buttons[0] = true", "fire 0.00 0.00"),
    ("", ""),
    ("pad.buttons[0] = false", "firestop"),
    ("pad.axes[3] = 0.9", "fire 0.00 0.00"),             # right trigger (3rd axis)
    ("pad.axes[3] = 0", "firestop"),
    ("pad.axes[4] = 0.5; pad.axes[5] = 0.8", "fire 0.50 -0.80"),  # twin-stick (4th/5th axis, Y inverted)
    ("pad.axes[4] = 0; pad.axes[5] = 0", "firestop"),
    ("pad.buttons[2] = true", "skill 0"),
    ("", ""),                                             # edge only
    ("pad.buttons[2] = false; pad.buttons[3] = true; pad.buttons[4] = true", "skill 3|skill 1"),
    ("pad.buttons[3] = false; pad.buttons[4] = false; pad.buttons[11] = true", "auto"),
    ("pad.buttons[11] = false; pad.axes[6] = -1", "move -1.00 0.00"),   # d-pad hat
    ("pad.axes[6] = 0", "move 0.00 0.00"),
    ("pad.buttons[0] = true", "fire 0.00 0.00"),
    ("pad.buttons[10] = true", "open UI_MissionPause|firestop"),         # pause stops fire
    ("pad.buttons[10] = false", ""),
    ("pad.buttons[10] = true", "close UI_MissionPause|fire 0.00 0.00"),
    ("pad.buttons[10] = false; pad.buttons[0] = false", "firestop"),
    ("failSkill = true; pad.buttons[1] = true", ""),                     # error -> disabled, no crash
    ("failSkill = false; pad.buttons[1] = false", ""),
    ("pad.buttons[1] = true", ""),                                       # stays disabled until Stop
    ("pad.buttons[1] = false; pad.h = -1", "move -1.00 0.00"),           # other actions still work
]


def main():
    lua = LuaRuntime(unpack_returned_tuples=True)
    lua.execute(MOCK)
    printed = []
    lua.globals().print = lambda *a: printed.append(" ".join(str(x) for x in a))
    lua.execute(open(os.path.join(HERE, "GamepadFight.lua"), encoding="utf-8").read())
    lua.execute("GamepadFight.Start(); take()")
    fails = 0
    for i, (setup, want) in enumerate(STEPS):
        lua.execute(setup + "\nframe()")
        got = lua.eval("take()")
        ok = got == want
        fails += not ok
        print("%s step %2d  %-70s -> %s" % ("ok  " if ok else "FAIL", i, setup, got if ok else "%r, want %r" % (got, want)))
    lua.execute("GamepadFight.Stop()")
    stop = lua.eval("take()")
    if stop != "move 0.00 0.00":
        fails += 1; print("FAIL stop:", stop)
    if not any("skill disabled" in p for p in printed):
        fails += 1; print("FAIL: no 'skill disabled' log")
    print("\n".join("  log: " + p for p in printed))
    print("RESULT:", "PASS" if not fails else "%d FAIL" % fails)
    sys.exit(1 if fails else 0)


if __name__ == "__main__":
    main()
