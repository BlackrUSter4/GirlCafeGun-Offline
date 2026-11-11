using SssjServer.Core;

namespace SssjServer.Game.Handlers;

/// <summary>
/// Two fixes for the avatar in the top-left corner and the profile screen it opens.
///
/// The avatar stayed a white box because UI_Player only sets it from the main girl in task (2,2), and only GM unlock
/// ever wrote that. Any account with a girl now gets one at login. Choosing another in-game still overwrites it.
///
/// The profile screen froze because UI_User.lua looks for Other/Content/List1/Grid/* while ui_user_prefab.u (the same
/// file in the build 5 and build 179 tables) has Other/Grid/* and no emoji tab. Find(...).gameObject threw partway
/// through OnInit, so the screen drew with no working buttons, including Back. No copy of the prefab with the newer
/// layout is available, so the screen's layout is corrected in the client at login. Its emoji tab stays unavailable.
/// </summary>
public sealed class ClientUiFixFeature : IGameFeature
{
    /// <summary>
    /// Moves Other/Grid to where the Lua expects it, before OnInit reads anything: the first Find or GetComponent does it,
    /// since OnInit also reads several paths with Transform:Find directly. The emoji tab is not in this prefab, so its
    /// inline setup gets hidden stubs and its data calls do nothing. Stubs carry a RectTransform from the start because
    /// adding a Toggle or Button to a plain Transform replaces it and kills every reference the Lua already holds.
    /// </summary>
    public const string UserScreenLua = @"pcall(function()
local function rect(name,parent)
  local go=CS.UnityEngine.GameObject(name)
  local rt=go:AddComponent(typeof(CS.UnityEngine.RectTransform))
  rt:SetParent(parent,false)
  rt.anchorMin=CS.UnityEngine.Vector2(0,0) rt.anchorMax=CS.UnityEngine.Vector2(1,1)
  rt.offsetMin=CS.UnityEngine.Vector2(0,0) rt.offsetMax=CS.UnityEngine.Vector2(0,0)
  return rt
end
local function reshape(root)
  local other=root:Find('Panel/Content/Other')
  local grid=other and other:Find('Grid')
  if grid and not other:Find('Content') then
    local content=rect('Content',other)
    local list1=rect('List1',content)
    grid:SetParent(list1,false)
    rect('Title2',content).gameObject:SetActive(false)
    local list2=rect('List2',content)
    rect('Grid2',list2)
    list2.gameObject:SetActive(false)
  end
  local tabs=root:Find('Panel/Toggle')
  if tabs and not tabs:Find('Tglbiaoqing') then
    local t=rect('Tglbiaoqing',tabs)
    t.gameObject:AddComponent(typeof(CS.UnityEngine.UI.Toggle))
    t.gameObject:SetActive(false)
  end
end
UI.__userPatch=function(c)
  if not c or c.__layoutFix then return end
  c.__layoutFix=true
  local baseFind,baseComp=c.Find,c.GetComponent
  local function stub(self,p,f)
    self.__stubs=self.__stubs or {}
    local key=tostring(f)..'|'..p
    local go=self.__stubs[key]
    if not go or go:Equals(nil) then
      go=rect(p,f and f.transform or self.pRootTrans).gameObject
      go:SetActive(false)
      self.__stubs[key]=go
    end
    return go
  end
  local function prepare(self)
    if self.pRootTrans and self.__fixedRoot~=self.pRootTrans then
      self.__fixedRoot=self.pRootTrans
      self.__stubs={}
      self.LoadBiaoqingCfg=function() end
      self.OpenEmojiPanel=function() end self.RefreshEmojiGroup=function() end
      reshape(self.pRootTrans)
    end
  end
  function c:Find(p,f)
    prepare(self)
    return baseFind(self,p,f) or stub(self,p,f).transform
  end
  function c:GetComponent(k,p,f)
    prepare(self)
    local r=baseComp(self,k,p,f)
    if r~=nil or not p or (p=='' and not f) then return r end
    local kind=CS.UnityEngine.UI[k] or CS.UnityEngine[k]
    if not kind then return r end
    local go=p=='' and f.gameObject or stub(self,p,f)
    return go:GetComponent(k) or go:AddComponent(typeof(kind))
  end
end
if not UI.__userFix then UI.__userFix=true
  local reg=UI.Register
  UI.Register=function(n,...) local c=reg(n,...) if n=='UI_User' then UI.__userPatch(c) end return c end
end
UI.__userPatch(UI.tbClass and UI.tbClass['UI_User'])
end)";

    /// <summary>
    /// The linkage event's map and level-info prefabs (UI_RailgunMap, UI_RailgunInfo) are in neither build's table, and no
    /// copy is available, so opening the event failed outright. This walks the same nodes the map would show, in order:
    /// the next playable level or a mission reward to claim, offered in a dialog. The levels, costs and rewards are the
    /// real ones; only the map is missing.
    /// </summary>
    public const string LinkageWithoutMapLua = @"pcall(function()
local L=LinkageActivityLogic
if not L or L.__noMap then return end
L.__noMap=true
function L:GetTower()
  local ok,msg=SystemUnlock:GetUnlock('UI_UnlockActivity36')
  if not ok then return Game.MessageBox.Simple(msg) end
  if not self:GetOpenActivity() then return Game.MessageBox.Simple(UIText('Error.20045')) end
  local pick
  for i=1,30 do
    local c=self:GetLevelConfig(i)
    if c and self:GetLevelState(i)==1 then
      if c.LevelType~=3 then pick=c break end
      local m=MissionManager:GetOtherAchieve(c.ExtendParam)
      if m and MissionManager:CheckInfoState(m,3)==1 then pick=c break end
    end
  end
  if not pick then return Game.MessageBox.Simple(UIText('UI_Activities.Tips')) end
  local go
  if pick.LevelType==3 then
    go=function() LuaCall:CallServer(LuaCall.Linkage_GetMissionReward,{1,pick.ExtendParam}) end
  elseif pick.LevelType==2 then
    go=function() self:EnterTower(pick.LevelId,pick.Diff,false) end
  else
    local kind=ActivitiesLogic.ActivityType.LinkageActivity
    go=function() UI.CloseAndOpen('UI_Formation',me.currentEquipSolutionId,function()
      LevelManager:SetCurrMapId(0,0,0,kind,pick.MapId)
      self:EnterTower(pick.LevelId,pick.Diff,false)
    end,kind,pick) end
  end
  local label=(string.gsub((pick.PreName or '')..' '..(pick.Name or ''),'\n',' '))
  Game.MessageBox.Dialog(UIText('tips'),label,UIText('cancel'),function() end,UIText('sure'),go)
end
end)";

    /// <summary>
    /// Team power is calculated only in the client (UI_Formation:ComputePowerValue), and mission condition 102 needs it.
    /// So a formation save also reports the total for the team just saved.
    /// </summary>
    public const string TeamPowerReportLua = @"pcall(function()
if LuaCall.__powerHook then return end
LuaCall.__powerHook=true
local send=LuaCall.CallServer
LuaCall.CallServer=function(self,cmd,param,...)
  local r=send(self,cmd,param,...)
  if cmd==LuaCall.Formation_Update then
    pcall(function()
      local ui=UI.GetClass('UI_Formation')
      if not ui or not ui.solution then return end
      local total=0
      for i=1,3 do
        local g=ui.solution:GetGroup(i-1)
        if g.MainCharacterCard~=nil then total=total+ui:ComputePowerValue(g) end
      end
      send(self,9998,{power=math.floor(total)})
    end)
  end
  return r
end
end)";

    /// <summary>
    /// Home screen layout with every event open. --all-events-open extends each past nEndTime, and banner configs use
    /// nEndTime too, so all ~125 historical banners went live at once: the page dots wrapped into a grid of dashes across
    /// the middle of the screen, and some banners (weapon pools 06-08) point at textures the device does not have.
    /// The list keeps the game's own order but drops banners whose texture fails to load, repeats of the same image, and
    /// anything past one row of dots (11). Concert and Moon-viewing Date share one slot (UI_Main turns Concert on at the end of OnOpen, just before ConcertNew), because the live game never ran
    /// both at once; while both are shown, the moon moves to the free sky between the girl and the banner.
    /// </summary>
    public const string HomeScreenLua = @"pcall(function()
local N=NormalActivityConfig
if N and not N.__bannerCap then
  N.__bannerCap=true
  local orig=N.GetEnabledBanner
  local good={}
  local function has(p)
    if not p or p=='' then return false end
    if good[p]==nil then local ok,t=pcall(function() return Game.LuaUtils.LoadTexture2D(p) end) good[p]=(ok and t~=nil) end
    return good[p]
  end
  function N:GetEnabledBanner()
    local out,seen={},{}
    for _,c in ipairs(orig(self)) do
      if #out>=11 then break end
      if not seen[c.sBg] and has(c.sBg) then seen[c.sBg]=true out[#out+1]=c end
    end
    return out
  end
end
local function fixMain(ui)
if N then
  local list=N:GetEnabledBanner()
  for _,fname in ipairs({'OnOpen','UpdatePage'}) do
    local f=ui[fname] local i,done=1,false
    while f do local n=debug.getupvalue(f,i) if not n then break end if n=='tbAdList' then debug.setupvalue(f,i,list) done=true break end i=i+1 end
    if done then break end
  end
  if ui.pRootTrans then
    local pages=ui:Find('Panel/Page')
    if pages then for k=0,pages.childCount-1 do pages:GetChild(k).gameObject:SetActive(k<#list) end end
    if ui.nAdIndex and ui.nAdIndex>#list then ui.nAdIndex=1 end
  end
end
if ui.__moonFix then return end
ui.__moonFix=true
local function place(self)
  if not self.pRootTrans then return end
  local moon=self:Find('Panel/BtnMoon') local con=self.btnConcert
  if not moon then return end
  self.__moonHome=self.__moonHome or moon.localPosition
  if con and con.activeInHierarchy and moon.gameObject.activeSelf then
    -- Local units under Panel, which the canvas scaler keeps independent of resolution. Converting from screen space
    -- through the canvas camera gave wrong results while UI_Main was still opening.
    moon.localPosition=CS.UnityEngine.Vector3(-96,280,moon.localPosition.z)
  else
    moon.localPosition=self.__moonHome
  end
end
for _,m in ipairs({'RefreshMidAutumn','ChangeFold','ConcertNew'}) do
  local f=ui[m]
  -- BtnMoon's DOTweenAnimation replays on every enable: a 'from' move with 0.7 s delay and 0.2 s duration that lands it
  -- back on its prefab spot, the same one as Concert. So place it again once that tween has finished.
  if f then ui[m]=function(self,...) local r=f(self,...) pcall(place,self)
    Game.Timer.Add(1.0,function() pcall(place,self) end) Game.Timer.Add(1.6,function() pcall(place,self) end) return r end end
end
pcall(place,ui)
end
-- Each load of UI_Main runs its script again and registers a fresh class table (UI.Register), which would drop these
-- hooks; the login story can close and reopen it. So the fix is applied every time that script loads.
-- The theme screen lists only entries whose Coverage names the client language (English is 4: 3 themes, part of the
-- backgrounds). Every theme's assets and every background image load on this build; the few background particle
-- effects that are missing are skipped by MainOrnament:UpdateSkinBg. So the list shows everything the server unlocks.
local function fixThemeTip(c) if c then c.CheckCoverage=function() return true end end end
fixThemeTip(UI.tbClass['UI_MainThemeTip'])
-- MainSkinBg.txt gives backgrounds 23-25 the name key of the row before; BackGround.23-25 hold their real names.
if MainOrnament and MainOrnament.tbSkinBg then
  for id=23,25 do local s=MainOrnament.tbSkinBg[id] if s and s.name=='BackGround.'..(id-1) then s.name='BackGround.'..id end end
end
if not UI.__homeLoadHook then
  UI.__homeLoadHook=true
  local load=UI._LoadLua
  UI._LoadLua=function(name,...)
    local r=load(name,...)
    if name=='UI_Main' and UI.tbClass['UI_Main'] then pcall(fixMain,UI.tbClass['UI_Main']) end
    if name=='UI_MainThemeTip' then pcall(fixThemeTip,UI.tbClass['UI_MainThemeTip']) end
    return r
  end
end
-- On a first login this can arrive before UI_Main is loaded, so wait for it.
local tries=0
local function wait()
  local ui=UI.GetClass('UI_Main')
  if ui then pcall(fixMain,ui) return end
  tries=tries+1
  if tries<60 then Game.Timer.Add(1,wait) end
end
wait()
end)";

    /// <summary>
    /// Screens reached from live buttons whose prefabs this client does not ship, found by the screen audit.
    /// <list type="bullet">
    /// <item>UI_MoodRules, opened by the mood icon on every formation card: this build renamed it UI_VigorRules
    /// ("Mood Info", opened from the top bar too), so the old name is sent there.</item>
    /// <item>UI_RailgunMain, opened by the linkage tile in the Fight event list: neither build has its prefab. It offers
    /// the event's two parts instead: the recruitment lottery and missions (UI_RailgunGacha, which does load) and the
    /// stages (the dialog from LinkageWithoutMapLua). Commands 404 and 405 behind the lottery are served.</item>
    /// <item>UI_RailgunGacha's OnInit reads four effect nodes under its Spine skeleton that this prefab lacks; empty
    /// placeholders let it finish.</item>
    /// <item>UI_VigorRules sits on layer 2, under any full screen, so each open lifts it above what is open.</item>
    /// </list>
    /// </summary>
    public const string MissingScreenLua = @"pcall(function()
if UI.__missingRedirect then return end
UI.__missingRedirect=true
local function linkage()
  local L=LinkageActivityLogic
  if not L then return end
  Game.MessageBox.Dialog(UIText('UI_RailgunMain'),'Lottery and missions, or stages?',
    'Recruit',function() UI.CloseAndOpen('UI_RailgunGacha') end,'Stages',function() L:GetTower() end)
end
local redirect={UI_MoodRules=function() UI.Open('UI_VigorRules') end, UI_RailgunMain=linkage}
local open,cao=UI.Open,UI.CloseAndOpen
-- UI_VigorRules is configured on layer 2, so over any full screen (formation, cafe) it drew behind the content.
local function raise(name)
  local v=UI.GetClass(name)
  if not v or not v.pRoot then return end
  local top=0
  for _,x in pairs(UI.GetOpenList()) do
    local n=type(x)=='table' and x.sClass or x
    local c=n~=name and UI.GetClass(n)
    local cv=c and c.pRoot and c.pRoot:GetComponent('Canvas')
    if cv and cv.sortingOrder>top then top=cv.sortingOrder end
  end
  local cv=v.pRoot:GetComponent('Canvas')
  if cv and cv.sortingOrder<=top then cv.sortingOrder=top+1 end
end
UI.Open=function(s,...)
  local f=type(s)=='string' and redirect[s]
  if f then return f(...) end
  local r=open(s,...)
  if s=='UI_VigorRules' then pcall(raise,s) end
  return r
end
UI.CloseAndOpen=function(s,...) local f=type(s)=='string' and redirect[s] if f then return f(...) end return cao(s,...) end
local load=UI._LoadLua
UI._LoadLua=function(name,...)
  local r=load(name,...)
  if name=='UI_RailgunGacha' and UI.tbClass[name] then
    local c=UI.tbClass[name] local init=c.OnInit
    c.OnInit=function(self)
      local spine=self:Find('Panel/Spine/girl304_2da01')
      if spine then
        local parent=spine
        for part in string.gmatch('SkeletonUtility-SkeletonRoot/root/zong/fanmaiji_1/Eff','[^/]+') do
          local nx=parent:Find(part)
          if not nx then local go=CS.UnityEngine.GameObject(part) go.transform:SetParent(parent,false) nx=go.transform end
          parent=nx
        end
        for i=1,4 do if not parent:Find('e_spine_railgun0'..i) then CS.UnityEngine.GameObject('e_spine_railgun0'..i).transform:SetParent(parent,false) end end
      end
      return init(self)
    end
  end
  return r
end
end)";

    // A STALLWATCH diagnostic once lived here: a Lua count hook that printed a traceback when the Game.Timer tick fell
    // behind. It was removed after the freeze was root-caused to the 32-bit native bridge (fixed by the arm64 build). The
    // hook could never catch that freeze — a native spin stops Lua entirely, so the hook cannot run — and it false-fired
    // whenever a cutscene legitimately paused Game.Timer. Pure log noise now that the real cause is understood.

    public string Name => "Avatar and profile screen fixes";

    public void Install(GameFeatureContext ctx)
    {
        ctx.OnPlayerPrepare((session, player) => EnsureMainGirl(player));
        ctx.OnPlayerLogin((session, player) =>
            ctx.LuaCalls.CallClient(session, GmConsoleFeature.DoLuaStringCmd, new { luaStr = UserScreenLua + " " + LinkageWithoutMapLua + " " + TeamPowerReportLua + " " + HomeScreenLua + " " + MissingScreenLua }));
    }

    internal static void EnsureMainGirl(Player player)
    {
        if (player.GetTask(GmUnlock.MainGirlGroup, GmUnlock.MainGirlTaskId) == 0 && player.Girls.Count > 0)
            player.SetTask(GmUnlock.MainGirlGroup, GmUnlock.MainGirlTaskId, (int)player.Girls[0].Girlid);
    }
}
