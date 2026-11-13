-- ============================================================
-- GamepadFight: Bluetooth/USB gamepad in shooting stages (offline build, gcg2#18, docs/06-CONTROLLER.md)
-- Reads the controller through Unity's legacy Input (Android InputDevice events) and drives the same C# calls
-- as the game's own developer keyboard handler Game.Keyboard.UpdateInput (stripped from release):
--   move  -> Game.FightJoystick.SetMoveOffset(dir)           fire -> fightCamera:OnFight(v) / OnFightStop()
--   skill -> Game.UI_FightProxy.OnUseSkillByIdx(0..2, nil)    dodge -> OnUseSkillByIdx(3, nil)
-- Touch keeps working: the pad only sends "stop" when it was the one that started an action.
-- Injected into UI_Fight.lua by tools/patch_lua_gamepad.py. Every C# call is guarded; an error disables only
-- that action and is logged once (logcat tag Unity, prefix [pad]).
-- ============================================================
GamepadFight = GamepadFight or {}
local P = GamepadFight

-- Unity joystick button numbers on Android (standard gamepad layout)
local BTN = { A = 0, B = 1, X = 2, Y = 3, LB = 4, RB = 5, L3 = 8, R3 = 9, START = 10, SELECT = 11 }

P.cfg = P.cfg or {
	dead      = 0.25,             -- stick dead zone
	trigger   = 0.45,             -- trigger press threshold
	fire      = { BTN.A, BTN.RB },-- hold to fire (plus right trigger)
	dodge     = { BTN.LB },       -- move skill (idx 3)
	skill1    = { BTN.X },        -- skill idx 0
	skill2    = { BTN.Y },        -- skill idx 1
	skill3    = { BTN.B },        -- skill idx 2
	pause     = { BTN.START },
	auto      = { BTN.SELECT },   -- auto-fight toggle
	twinStick = true,             -- right stick = aim + fire
	-- Raw axis numbers (gcgpad_aN = Unity "Nth axis", added by apk-build/patch_inputmanager.py). Defaults follow the
	-- game's own InputManager (RightHorizontal = 4th, RightVertical = 5th, HandleFire = 3rd axis). Android pads differ:
	-- turn on debug (L3+R3) and read logcat to find yours. One axis per role, no overlaps.
	rStickX   = 4, rStickY = 5,
	rTrigger  = 3,
	hatX      = 6, hatY = 7,   -- d-pad as hat axes [INFER], 0 = off
	debug     = false,            -- L3+R3 toggles: logs every button / axis change
}

local KC_JOY0 = 330   -- KeyCode.JoystickButton0
local kc = {}
local function key(n)
	local k = kc[n]
	if not k then k = U3D.KeyCode.__CastFrom(KC_JOY0 + n); kc[n] = k end
	return U3D.Input.GetKey(k)
end
local function anyKey(list)
	for _, n in ipairs(list) do if key(n) then return true end end
	return false
end
local function axis(n)
	if not n or n <= 0 then return 0 end
	local ok, v = pcall(U3D.Input.GetAxisRaw, "gcgpad_a" .. n)
	return ok and v or 0
end

local broken = {}
local function try(name, fn, ...)
	if broken[name] then return end
	local ok, err = pcall(fn, ...)
	if not ok then
		broken[name] = true
		print("[pad] " .. name .. " disabled: " .. tostring(err))
	end
end

-- ---------- game actions (same calls as Game.Keyboard.UpdateInput) ----------
local function fightCamera()
	local gm = Game.GameMode.instance
	return gm and gm.fightCamera
end
local function move(x, y)
	Game.FightJoystick.SetMoveOffset(U3D.Vector2(x, y))
end
local function fireStart(x, y)
	local cam = fightCamera(); if cam then cam:OnFight(U3D.Vector2(x or 0, y or 0)) end
end
local function fireStop()
	local cam = fightCamera(); if cam then cam:OnFightStop() end
end
local function skill(idx)
	Game.UI_FightProxy.OnUseSkillByIdx(idx, nil)
end
local function pause()
	if Game.Map.isOnline or Game.CartoonMgr.IsPlaying or Game.GameMode.IsEnd then return end
	local gm = Game.GameMode.instance
	if not (gm and gm.isBegin) then return end
	if UI.IsOpen("UI_MissionPause") then UI.Close("UI_MissionPause") else UI.Open("UI_MissionPause") end
end
local function autoFight()
	local ui = Game.UI_Fight.Ins
	if ui then ui:OnClickBtnAuto() end
end
-- ---------- state ----------
local st = { moving = false, firing = false, prev = {}, prevHat = 0, dbgAxes = {} }

local function pressed(name, list)
	local now = anyKey(list)
	local was = st.prev[name]
	st.prev[name] = now
	return now and not was
end

local function debugDump()
	for n = 0, 19 do
		local now = key(n)
		if now ~= (st.dbgAxes["b" .. n] or false) then
			st.dbgAxes["b" .. n] = now
			print(string.format("[pad] button %d %s", n, now and "down" or "up"))
		end
	end
	for n = 1, 28 do
		local v = axis(n)
		local q = math.floor(v * 4 + 0.5) / 4
		if q ~= (st.dbgAxes[n] or 0) then
			st.dbgAxes[n] = q
			print(string.format("[pad] axis %d = %.2f", n, v))
		end
	end
end

function P.Tick()
	local cfg = P.cfg
	-- L3 + R3: debug log on/off
	if pressed("dbg", { BTN.R3 }) and key(BTN.L3) then
		cfg.debug = not cfg.debug
		print("[pad] debug " .. tostring(cfg.debug) .. ", joysticks: " ..
			table.concat((function() local t = {} local n = U3D.Input.GetJoystickNames()
				for i = 0, n.Length - 1 do t[#t + 1] = n[i] end return t end)(), ", "))
	end
	if cfg.debug then debugDump() end

	-- pause works even while the fight UI is hidden
	if pressed("pause", cfg.pause) then try("pause", pause) end
	if UI.IsOpen("UI_MissionPause") then P.Reset(); return end

	-- move: left stick (stock Horizontal/Vertical include joystick axes 1/2) or d-pad hat
	local x, y = U3D.Input.GetAxisRaw("Horizontal"), U3D.Input.GetAxisRaw("Vertical")
	if x * x + y * y < cfg.dead * cfg.dead then
		local hx, hy = axis(cfg.hatX), axis(cfg.hatY)
		x, y = hx, -hy
	end
	if x * x + y * y >= cfg.dead * cfg.dead then
		local m = math.sqrt(x * x + y * y)
		try("move", move, x / m, y / m)
		st.moving = true
	elseif st.moving then
		try("move", move, 0, 0)
		st.moving = false
	end

	-- fire: buttons, right trigger, or right stick (twin-stick aim)
	local ax, ay = 0, 0
	local aimed = false
	if cfg.twinStick then
		local rx, ry = axis(cfg.rStickX), axis(cfg.rStickY)
		if rx * rx + ry * ry >= cfg.dead * cfg.dead then ax, ay, aimed = rx, -ry, true end
	end
	local trig = axis(cfg.rTrigger)
	local wantFire = aimed or anyKey(cfg.fire) or trig > cfg.trigger
	if wantFire and not st.firing then
		try("fire", fireStart, ax, ay)
		st.firing = true
	elseif not wantFire and st.firing then
		try("fire", fireStop)
		st.firing = false
	end

	-- skills (edge-triggered)
	if pressed("dodge", cfg.dodge) then try("dodge", skill, 3) end
	if pressed("s1", cfg.skill1) then try("skill", skill, 0) end
	if pressed("s2", cfg.skill2) then try("skill", skill, 1) end
	if pressed("s3", cfg.skill3) then try("skill", skill, 2) end
	if pressed("auto", cfg.auto) then try("auto", autoFight) end
end

function P.Start()
	if P.timer then return end
	P.Reset()
	-- never break the fight HUD: a failure here only turns the gamepad off
	local ok, t = pcall(Game.Timer.Add, 0.01, 0.01, function() try("tick", P.Tick) end)
	if ok then P.timer = t; print("[pad] started") else print("[pad] start failed: " .. tostring(t)) end
end

function P.Reset()
	if st.moving then try("move", move, 0, 0) end
	if st.firing then try("fire", fireStop) end
	st.moving, st.firing = false, false
end

function P.Stop()
	if P.timer then pcall(Game.Timer.Delete, P.timer); P.timer = nil end
	P.Reset()
	broken = {}
	print("[pad] stopped")
end
