# 08 — Online resources: sweep, local fixes, ledger (2026-11-12)

The offline build must not need the publisher's servers. This file lists every online address the
English client (EN v1.0.9, environment `en`) can still ask for, what is now served locally, and what
cannot be fixed.

Trigger: the main-menu **Notice** window showed an Alibaba OSS error page
(`<Code>NoSuchKey</Code>`, host `japan-static-oss.bilibili.co…`, key
`game.overseas-sdk-web.game-global-sdk/girlcafegun/notice/h5/…`).

## 1. How the sweep was done

| Source | How | Result |
|---|---|---|
| `envir.json` in the OBB (`assets/envir.json`), stock copy `assets/obb/envir.json.stock` | read environment `en` | 2 page addresses, section 2 |
| Client Lua (`assets/device_assets/overrides/lua.u`, 831 scripts, decrypted) | search for `http`, `OpenURL`, `OpenUrl`, `uniwebview:Load` | sections 2 and 3 |
| Client tables (`setting.u` in the OBB, 1,143 tables; same bytes as `server/setting/*.txt` for the 6 files below) | search for `http` | `BannerConfig`, `Riki`, `localurl`, `ActivityTemplate1`, `ActivityTemplate6`, `SelectZone` |
| Language table (`overrides/lang.u`) | search for `http` | 1 address, plain text in the user agreement (`https://game.bilibili.com/sssj/`), not a link |
| C# strings (`global-metadata.dat` in the APK) | search for `http`, known hosts | only test tools (UWA, UBox upload); no game web pages |
| Java SDKs (`classes*.dex`, `assets/sdk_config.properties`) | host strings | section 4 |
| Server (`server/src`) | search for `http` | no outgoing requests |
| Live check | one `curl` per address, 2026-11-12 | column "Now" below |
| Wayback Machine | CDX index per host, 2026-11-12 | column "Archive" below |

Not done: a network capture on the phone (the phone was not on USB). Section 4 comes from static
strings only. `[INFER]` marks what was not seen at run time.

## 2. Fixed: pages now served locally

| Window | Original address (`envir.json`, `en`) | Now | Archive | Local replacement |
|---|---|---|---|---|
| Notice (main menu, `UI_Notice.lua:193`) | `notice` = `https://game-intl.biligame.net/girlcafegun/notice/h5/` | 404, OSS `NoSuchKey` | never archived | `http://127.0.0.1:9080/notice.html` → `server/src/SssjServer.Core/Web/notice/index.html` |
| Server error (`UI_ServerError.lua:54`; opened from `UI_Login.lua:570,729,752`, `NoticeCommon.lua:78`) | `errUrlServerlist`, `errUrlGate`, `errUrlServer` = `https://game-intl.biligame.net/girlcafegun/notice/maintenance/h5/` | 404, OSS `NoSuchKey` | never archived | text embedded in Lua (`OfflineErrorHtml` in `Lib.lua`), shown with `UniWebView.LoadHTMLString`; needs no server |
| Help, Glossary (`UI_User.lua:567,570`; buttons hidden in English) | `help`, `word` = the same maintenance address | 404 | never archived | `http://127.0.0.1:9080/web/maintenance/index.html` → `Web/maintenance/index.html` |

How it works:

- **Server**: files under `server/src/SssjServer.Core/Web/` are embedded in `SssjServer.Core.dll`
  (`EmbeddedResource` in the csproj) and served under `/web/` by `WebAssets.cs` +
  `BootstrapHttpServer.Route`. `/notice.html` serves `Web/notice/index.html`. No packaging change:
  the pages travel inside the server DLL in `libsssjdata.so`.
- **Notice page**: tabs All / Notices / Events / News, a list and a detail view. It reads
  `/notice.txt` of the same server (`BootstrapHttpServer.Notice()`), so there is one place to edit
  notice text. Layout follows the archived sibling pages of the same Bilibili template (section 5).
  The original English notice texts are lost; the page shows what `Notice()` returns.
- **Client**: `tools/patch_lua_offline_web.py` (idempotent) changes `lua.u`:
  1. `Lib.lua`: adds `OfflineWebUrl(url)` and `OfflineErrorHtml()`. `OfflineWebUrl` maps the two dead
     addresses to the local server. It takes `scheme://host:port` from `Game.Dir.GetBaseUpdateUrl()`
     (on the phone that base is `http://127.0.0.1:9080/10009/`: the client adds the version code;
     logcat 2026-11-12), fallback `http://127.0.0.1:9080/`.
  2. All 12 `uniwebview:Load(X)` calls in `UI_Notice`, `UI_User`, `UI_OtherTip`, `UI_RikiComic`
     become `uniwebview:Load(OfflineWebUrl(X))`.
  3. `UI_ServerError` calls `LoadHTMLString(OfflineErrorHtml(), "", false)`; if that call fails, it
     loads the mapped address.
- `envir.json` in the OBB is unchanged (4+ GB file; the Lua override is 3 MB). Its `notice`, `help`,
  `word`, `errUrl*` entries still name Bilibili, but no Lua path loads them unmapped.

Install order: `lua.u` + `versioncrc.json` go to `files/` (data package or `assets/sync_phone.ps1`).
The new Notice page needs the rebuilt APK. A new `lua.u` with an old APK still works: the old server
answers `/notice.html` with its placeholder page.

Checks done on the PC (2026-11-12): server build, `GET /notice.html`, `/web/notice/`,
`/web/maintenance/index.html`, `/notice.txt` = 200; page rendered in a browser with no console error;
patched Lua compiles (lupa); `OfflineWebUrl` unit-checked with a mocked `Game`; CRC in
`versioncrc.json` equals CRC32 of `lua.u`.

Checks done on the phone (SM-S926B, 2026-11-12, APK from commit `a9fba3d` + this `lua.u`):
`adb install -r` = Success; `lua.u` and `versioncrc.json` pushed, md5 equal to the PC files; logcat
`重新计算文件crc值: lua.u 734738852` (CRC accepted) and Lua runs (`NoticeCommon's Url is …`), no Lua error;
through `adb forward` the phone's server answers `/notice.html` (200, 6,830 bytes) and
`/web/maintenance/index.html` (200). **Not checked:** the Notice window itself on screen (needs a tap
in the game) and the server-error window.

## 3. Ledger: addresses the client can open that are not fixed

"Opens" = the phone's browser unless noted. English = reachable with language `en_US`.

### 3.1 Reachable in English

| # | Where (source) | Address | Now (2026-11-12) | Archive | Why not fixed |
|---|---|---|---|---|---|
| 1 | Settings → Other → social buttons (`UI_SocialBtns.lua:5`) | `https://www.facebook.com/girlcafegunofficial` | 200 | — | live external page |
| 2 | same (`:6`) | `https://twitter.com/cafe_gun` | 200 (→ x.com) | — | live external page |
| 3 | same (`:7`) | `https://discord.gg/s6kWCzaMgS` | 200 (invite page; invite validity not checked) | — | live external page |
| 4 | same (`:8`) | `https://www.youtube.com/channel/UCXsux0c1CjoOICiF_S8MdqA` | 200 | — | live external page |
| 5 | same (`:9`) | `https://www.reddit.com/r/GirlCafeGun/` | 403 to curl (bot wall) `[INFER]` live | — | live external page |
| 6 | same (`:10`) | `https://www.instagram.com/girlcafegunofficial/` | 200 | — | live external page |
| 7 | "Rate the game" window (`UI_Evaluate.lua:120`, opened by `EvaluateGuide.lua:38`) | `https://play.google.com/store/apps/details?id=com.biligameglobal.girlcafegungp` | 404 (app delisted) | not checked | a store page cannot be local. Option: stop `EvaluateGuide` from opening the window |
| 8 | Main banner 18 "survey" (`BannerConfig` row Id 18, `ShowType` 1; `DropWay.lua:243`) | `https://docs.google.com/forms/d/e/1FAIpQLSevCq76gs6OF2HooAP89RdtiaZ3gpyg0SjTWloMs00RjJESuA/viewform` | 200, form closed | not checked | survey is closed. `[INFER]` shown only if the server lists this banner; not seen on the phone |
| 9 | Main banner 31 "wiki" (`BannerConfig` row Id 31, remark "JP server"; `UI_User.lua:581`, `UI_JPTip.lua:54`) | `https://girlcafegun.boom-app.wiki/` | DNS dead | 563 HTML pages archived (Japanese) | Japanese fan wiki; `[INFER]` not shown in English |
| 10 | Settings → Other → Terms / Privacy (`UI_User.lua:521-544`) | Bilibili SDK call `showAgreementWithLicence` / `showAgreementWithPrivacy` | `[INFER]` no-op or SDK error with `platform=selftest` | — | page lives inside the SDK; address not in the client |
| 11 | Settings → Other → Support (`UI_User.lua:552`) | SDK call `openCustomerServiceCenter` (AiHelp, `bilibiligame.aihelp.net`) | `[INFER]` dead, service closed | — | third-party help desk |
| 12 | Payment callback | `chargeCallback` = the maintenance address | 404 | — | not used: purchases are granted by the server (`tools/patch_lua_free_purchase.py`) |

### 3.2 In the client, not reachable in English (other regions or hidden rows)

| Where (source) | Address | Now | Archive |
|---|---|---|---|
| Album → comic, JP row (`Riki` row 9002, region list hides it for English; `UI_RikiComic.lua:43`) | `https://gcg.xoyo.jp/comic/` | DNS dead | 1 capture, `web.archive.org/web/20200921002122/https://gcg.xoyo.jp/comic/` (page shell, 3.4 KB) |
| Album → comic, CN row (`Riki` row 9001) | `https://manga.bilibili.com/m/mc27419/383240` | not checked | not checked |
| Banner 6 (`BannerConfig` Id 6, `ShowType` 0 = hidden) | `https://gcg.xoyo.com` | DNS dead | 600+ captures (2016, older game of the same name) |
| Activity page row 28 (`ActivityTemplate1`) | `https://zh.surveymonkey.com/r/88KFHHY?ID=%s` | not checked | not checked |
| Activity page row 62 (`ActivityTemplate6`) | `https://mall.bilibili.com/ip.html?...ip=0_3100087...` | not checked | not checked |
| JP notice (`UI_Notice.lua:175-187`) | `https://ingame-gcg.xoyo.jp/news/index.html`, `https://stg-ingame-gcg.xoyo.jp/news/index.html` | DNS dead `[INFER]` (same domain as gcg.xoyo.jp) | not checked |
| TW notice (`UI_Notice.lua:165`) | `https://www.komoejoy.com/GirlCafeGun/notice/maintenance/h5` | not checked | archived, section 5 |
| JP FAQ / support (`UI_User.lua:556`, `UI_JPTip.lua:41-48`, `GameManager.lua:342`) | `https://gcg.xoyo.jp/faq/index.html`, `https://contact.marv.jp/...` | DNS dead / not checked | FAQ: `web.archive.org/web/20200920232611/https://gcg.xoyo.jp/faq/index.html` |
| KR links (`UI_User.lua:621`, `UI_Charge.lua:100`, `UI_KRFirst.lua:45-47`) | `cafe.naver.com/girlcafegunkr`, `game.biligame.kr/sdk/...` | not checked | not checked |
| CN links (`UI_User.lua:608,613,2394`, `UI_AnniversaryMain.lua:28`, `UI_2ndAnniversaryMain.lua:43`, `localurl` table) | `game.bilibili.com/sssj/...`, `game.bilibili.com/kf/`, `member.bilibili.com/...`, `itunes.apple.com/cn/app/id1444721122` | `game.bilibili.com/sssj/` = 200 | `game.bilibili.com/sssj/2021znhy/h5/` archived 2021-11-28 |
| Account migration (`UI_Login.lua:167`) | `https://service.xoyo.com/index.php?r=sssj/migrate-form` | not checked | not checked |
| Other store pages (`UI_Evaluate.lua:116-132`) | Play Store / App Store pages of the TW, KR, JP, US-iOS builds | not checked | not checked |
| Developer server list (`UI_ServerURL.lua:10-15`, `localurl` `tburl1-3`) | internal test servers | — | — |
| Other environments in `envir.json` (`dev`, `stg`, `product`, `seasun_jp`, `bili`, `tc`, `kr`, …) | `*.marv-games.jp`, `*.xoyo.jp`, `*.bilibiligame.net`, … | — | — (only `en` is used: `"cur": "en"`) |

### 3.3 Already local before this change

| What | Original | Local |
|---|---|---|
| Server list, state, update check | `https://l1x-prod-{us,eu,sgp}-serverlist-sssj.bilibiligame.net` (`envir.json.stock`) | `http://127.0.0.1:9080` (`/serverlist`, `/getstate`, `/checkupdate`, `/uploadclientip`) |
| Asset updates | `https://cdn-cafe2.xoyo.games/dev_en` (DNS dead, nothing archived) | none needed; missing bundles: `docs/06-MISSING-ASSETS.md` |
| Notice data (`NoticeCommon.lua:16-21`) | `{updateUrl}notice.txt` (default `http://gcg2notice.ks3-cn-beijing.ksyun.com/notice.txt`) | `GET /notice.txt` |
| Bilibili SDK config | `/gapi/client/config` | answered by the local server |

### 3.4 Local request that fails

| What | Request | Result | Note |
|---|---|---|---|
| Native notice data (`NoticeCommon.lua:18-36`, C# `NoticeHelper`) | `GET http://127.0.0.1:9080/10009/notice.txt` (logcat: `NoticeCommon's Url is http://127.0.0.1:9080/10009/notice.txt`) | 404: the server routes `/notice.txt` only | The client adds the version code `10009/` to the update base. The native notice lists (banners, login notice, red dot) therefore stay empty. Not changed here: turning it on changes untested game behaviour (gcg2#28). The web Notice page is not affected; it asks for `/notice.txt`. |

## 4. Ledger: SDK background traffic (cannot be served locally)

These libraries are inside the stock APK and can call home on their own `[INFER: static strings only,
no capture]`. They carry no game content. The game is built to run without a network
(`docs/03-BUILD-APK.md`, section "Airplane mode").

| SDK | Hosts (source: `assets/sdk_config.properties`, `classes*.dex`, `server/setting/SelectZone.txt`) | Purpose |
|---|---|---|
| XGSDK / Bilibili global SDK | `l13-prod-{us,eu,sgp}-xgsdk-sssj.bilibiligame.net`, `…-data-sssj…`, `…-activity-sssj…`, `prod-us-param-sssj.bilibiligame.net`, `console.xgsdk.com`, `a2.xgsdk.com`, `p2.xgsdk.com`, `xgdata.xgsdk.com`, `game-intl.biligame.net/return_url` | login, payment, statistics (login is bypassed: `platform=selftest`) |
| AiHelp | `bilibiligame.aihelp.net`, `cdn.aihelp.net` | customer support window |
| AppsFlyer | `*.appsflyer.com` (host templates in dex) | install attribution |
| Firebase / Google | `firebase-settings.crashlytics.com`, `reports.crashlytics.com`, `app-measurement.com`, `www.googleapis.com`, `girlcafegunglobal.appspot.com` | analytics, crash reports, push |
| Facebook SDK | `graph.facebook.com`, `www.facebook.com` | login, events |
| Twitter SDK | `api.twitter.com` | login/share |
| Crasheye | host not in clear text (`libCrasheyeNDK.so`) | crash reports |
| Tencent GCloud Voice / TerSafe | `assets/GCloudVoice/config.json`, `libtersafe2.so` | voice chat, anti-cheat |
| UWA, UBox | `uwa-public.oss-cn-beijing.aliyuncs.com`, `ubox-outside-upload.testplus.cn` | developer profiling, off in release |

Removing `android.permission.INTERNET` is **not** an option: the client reaches `127.0.0.1` through
the same permission. To silence the SDKs, each one needs a stub in the dex. Not done.

## 5. Recovery notes (Wayback Machine, 2026-11-12)

- `game-intl.biligame.net/girlcafegun*`: **0 captures**. The English notice and maintenance pages and
  their notice texts are not archived. Only SDK pages of that host exist (`/sdk/verification/`,
  `/email_verify/result.html`).
- The page was a Vue app of Bilibili's shared notice template. Two siblings are archived:
  - Taiwan: `web.archive.org/web/20220519101721/https://www.komoejoy.com/GirlCafeGun/notice/maintenance/h5/`
    with `static.komoejoy.com/GirlCafeGun/notice/maintenance/h5/index.js?2ac65a2a` and 13 images
    (`contentBox.png`, `nav1-4.png`, `nav1-3_on.png`, `tag1-2.png`, `back.png`, `leftArr.png`, `rightArr.png`).
  - China: `web.archive.org/web/20260913130429/https://game.bilibili.com/sssj/notice2/h5/` with
    `static.biligame.com/sssj/notice2/h5/index.js?8325c4c2`, 4 images and the font `yuehei.woff2`.
- Template behaviour (from the Taiwan `index.js`): 4 tabs (`navList: [all, 3, 2, 7]`), a paged list
  and a detail view. Data came from `//l11-web-api.komoejoy.com/game/news/page`
  (`page_num, page_size=10, game_base_id=103872, region=3, show_position=2, type`),
  `/game/news/detail?news_id=…` and `/game/banner/list`. Some Taiwan API replies are archived
  (for example `…/game/banner/list?game_base_id=103872&region=3&show_position=2`, 2022-05-19,
  and `…/game/news/detail?news_id=181588232765089995&region=3`). They are Traditional Chinese.
- The English API host and `game_base_id` are unknown (the English `index.js` is not archived). A CDX
  search of `biligame.net` for `/game/news/` was refused (rate limit) and is open.
- The archived images were **not** copied into the build: the Wayback Machine answered 429
  ("suspected abusive bot traffic") after the index queries, and the tab images carry Chinese text.
  The local page is a new English page with the same structure.
- English official site: `web.archive.org/web/20210706165424/https://girlcafegun.biligames.com/` and
  `static-intl.biligame.net/girlcafegun/gw/…` (243 captures: site script, backgrounds, voice samples).
  Not used by the client.

## 6. Open items (board cards)

1. Phone test on screen: Notice window, and the server-error window (`LoadHTMLString` path) — gcg2#22.
2. Real notice texts: none recovered; retry the Wayback search for the English notice API — gcg2#23.
3. Rate window with a dead store link — gcg2#24.
4. Two banners with dead links — gcg2#25.
5. Terms / Privacy / Support buttons call the SDK — gcg2#26.
6. SDK background traffic: capture, then decide on stubs — gcg2#27.
7. Native notice data: `/10009/notice.txt` is 404 — gcg2#28.
