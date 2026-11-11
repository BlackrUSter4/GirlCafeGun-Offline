-- G213950713
local function P(...) local t={} for i=1,select('#',...) do t[i]=tostring(select(i,...)) end print('G213950713 '..table.concat(t,' ')) end
local ok, err = xpcall(function()
-- Count card + weapon gacha pools and how many are currently open/drawable.
local openCard,totalCard=0,0
local pools=GachaCommon and GachaCommon:GetAllPools and GachaCommon:GetAllPools() or nil
P('GachaCommon', tostring(GachaCommon~=nil), 'GetAllPools', tostring(pools~=nil))
-- Fallback: inspect gacha config directly
local cfg = Game.GachaMgr or GachaLogic or GachaCommon
local names={}
for k,v in pairs(_G) do if type(k)=='string' and k:lower():find('gacha') then names[#names+1]=k end end
P('gacha globals:', table.concat(names,','):sub(1,300))

end, debug.traceback)
if not ok then print('G213950713 ERR '..tostring(err)) end