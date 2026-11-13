# 05 - Assets recovered from other packages

Date: 2026-11-12. Source: the xapk files in `OtherAPKs/` (EN 1.0.2, EN 1.0.6, EN 1.0.7, JP 1.1.14).
Compared by entry name, size and CRC against `assets/obb/` and `assets/Bundles/`. The bundles were not opened.

## Added to `assets/Bundles/` (16 bundles, taken from the EN 1.0.7 OBB, none existed before)
- `wwisedata/theme_song_english.u` (3,126,342 bytes), `wwisedata/theme_song_korea.u` (7,020,190 bytes)
- `charact/player.u` (548,640 bytes)
- `update/`: `cafe1__tex.u`, `cafe2_.u`, `cafe3_.u`, `cafe4_.u`, `cb005__tex.u`, `cb2_.u`, `cb2__l2d_6.u`,
  `cbt5__l2d_12.u`, `l2d1__l2d_3.u`, `l2d2__l2d_4.u`, `l2d3__l2d_5.u`, `ob_.u`, `qlzl2d__l2d_7.u`

Five of them are on the missing list in `04-MISSING-ASSETS.md` (all in the not-required group):
both theme songs, `update/cafe1__tex.u`, `update/cafe2_.u`, `update/cb2_.u`.
None of the 18 story-voice bundles is in any package.

## Kept apart in `assets/old-versions/<version>/` (older builds, not used by the game, not in git)
Same relative paths as in the game. Nothing in `assets/obb/` or `assets/Bundles/` was overwritten.
- Title screen and login, all four versions: `ui/ui_login_prefab.u`, `ui_tx/ui_login.u`, `2danimation/bglogin02_2da.u`,
  `wwisedata/english(us)/new_login.u` (not in 1.0.2), `wwisedata/recordpath_json_bytes.u`.
  - `ui/ui_login_prefab.u`: 85,701 (1.0.2), 85,903 (1.0.6, 1.0.7), 82,678 (JP); ours 47,945.
  - `2danimation/bglogin02_2da.u`: 30,644 (1.0.2); ours 1,601,819.
  - `wwisedata/english(us)/new_login.u`: 15,208,144 (1.0.6, 1.0.7, JP); ours 15,312,564.
- Scripts and tables, all four versions: `lua.u`, `lang.u`, `setting.u` (JP has Japanese text). `version.zip` from 1.0.2 and JP.
- EN 1.0.2 only: `first_ctrl.u`, `first_fbx.u`, `first_mat.u`, `first_other.u`, `first_prefab.u`, `first_shader.u`,
  `wwisedata/theme_song_english.u`.
- EN 1.0.6 only: `update/cb2_.u` (891,884) and `update/ob_.u` (4,381,397); these differ from the 1.0.7 copies in `assets/Bundles/`.
- JP 1.1.14 only: `lv_1_0/` (10 bundles, about 62 MB).

## Not copied
- About 1,370 other bundles per EN OBB differ in size from ours (mostly `ui`, `at`, `p_skill`, `ui_tx`, `p_gun`): older builds of bundles we have.
- About 1,040 more differ only by a 4-byte build stamp.
- The 11 mp4 files in the JP OBB are already in `assets/Bundles/Raw/`.
- `[INFER]` "earlier title screen" rests on the size differences only.
