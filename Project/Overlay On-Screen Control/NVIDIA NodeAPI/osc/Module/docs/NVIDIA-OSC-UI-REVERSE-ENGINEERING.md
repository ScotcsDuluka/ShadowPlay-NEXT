# ShadowPlay NEXT — NVIDIA OSC UI: Reverse Engineering / Architecture Atlas

**ตรวจ Source:** 25 ตุลาคม 2026 · **เครื่อง:** AORUS-DESKTOP · **ขอบเขต:** `osc/`, `NVIDIA NodeAPI/index.js`, Native Host `NVIDIA OSC/src/osc_main.cpp`

> เอกสารฉบับนี้เป็น **Static Source Atlas**; ไม่ได้ยืนยันว่า UI ทุกหน้าเปิด/คลิก/เรนเดอร์บน CEF จริงแล้ว
> ก่อนแก้ UI **ต้องอ่าน** [อ่านก่อนทำ UI.md](อ่านก่อนทำ%20UI.md) และ `../AGENTS.md`; ค้นรายชื่อทั้งหมดจาก [UI Component Catalog](NVIDIA-OSC-UI-COMPONENT-CATALOG.md)
> สถานะจริงของ Source กับ `Build/NVIDIA ShadowPlay` และ Runtime อาจต่างกัน: ห้ามอ้างว่า Deploy/CEF ผ่านเพียงเพราะมีไฟล์อยู่
> การตรวจนี้ไม่แก้ source UI, Native Host, Capture, Hook และไม่ Restart/Deploy

## 1) แผนภาพสถาปัตยกรรมจริง

```text
NVIDIA OSC Native C++ Host (NVIDIA OSC/src/osc_main.cpp)
  ├─ CEF 73 / off-screen rendering / OSR / input (mouse-keyboard)
  ├─ OnContextInitialized → browser/window setup
  ├─ GetViewRect / OnPaint / OnAcceleratedPaint → UI pixels → GPU/CPU compositor
  ├─ HandleOscQuery / OnQuery + native bridge /cefquery
  └─ injected mod hot-loader → GET /mods/list ทุก ~2 วินาที
       │
       ▼
NodeAPI HTTP server (NVIDIA NodeAPI/index.js)
  ├─ เสิร์ฟ OSC page/assets และ /mods/list (explicit allowlist)
  ├─ /cefquery bridge และ API routes/shims
  └─ source assets ใน NVIDIA NodeAPI/osc/
       │
       ▼
osc/index.html [ng-app="main", ng-csp, ng-strict-di]
  ├─ cefquery-shim.js (fallback bridge เฉพาะเมื่อไม่มี window.cefQuery)
  ├─ vendor.js (AngularJS 1.5.5 + Angular Material 1.1.3)
  ├─ common.js + app.js (NVIDIA Angular modules, routes, directives, CSS)
  ├─ HEAD: pre-bootstrap mods (บาง Route + V5 + Motion + Background)
  ├─ BODY: user-config.js + config.js + localization + helper scripts
  ├─ <div ui-view> → UI-Router page
  └─ Runtime: allowlisted mods ที่ Native Host injects เพิ่ม
       ├─ JS/CSS pages/panels + Angular $compile/DOM enhancement
       ├─ Main-menu V2 / Unified V4 / NEXT V5 ownership
       ├─ Gallery / GPU / ReShade / Capture / Motion / Notifier
       └─ l10n + focus/input + native state guards
```

**สำคัญ:** มีอย่างน้อย 3 กลไกสร้าง UI ที่ต้องแยกกัน: **(1) Native UI-Router + NVIDIA directive, (2) custom Angular State/Controller, (3) DOM injection/$compile/MutationObserver**. อย่าถือว่าไฟล์ทุกไฟล์ใน `mods/` ถูกโหลดทัน Angular bootstrap หรือใช้ได้เหมือน Route

## 2) ตัวเลข Source ที่นับจากไฟล์จริง

| Inventory | ผลค้นปัจจุบัน | วิธีตรวจ/ข้อจำกัด |
|---|---:|---|
| NVIDIA `app.js` | 73 Angular directives / 52 controllers / 26 services / 1 factory | regexp `.directive/.controller/.service/.factory`; static match |
| Native UI-Router States ใน `app.js` | 38 | `.state(...)` ที่พบใน bundle; หนึ่ง state เป็น abstract `main` |
| Custom UI-Router States ใน top-level Mods | 6 | `advanced-capture-route`, `gpu-profiles-route`, `main-menu-live`, `osc-fps`, `osc-hook-settings`, `osc-renderer` |
| Material Component module names ใน `vendor.js` | 42 | มี code/module; ไม่ได้แปลว่าทุกตัวถูกทดสอบบน CEF |
| Native `md-*` tags ใน `app.js` | 25 tag names | ไม่นับ internal Angular Material tags จาก vendor |
| Native `nv-*` tags ใน `app.js` | 60+ tag names | บางตัวเป็น sub-elements / ไม่ใช่ directive ใช้ลอย ๆ |
| NVIDIA icon glyph CSS mapping ใน `app.js` | 88 class mappings | `nvgcshare.woff2` + บาง icon เป็น asset |
| top-level `mods/*.js` | 32 | ตรวจ syntax `node --check` 32/32 ผ่านในรอบนี้ |
| top-level `mods/*.css` | 20 | CSS จำนวนมากไม่ได้ link โดย `index.html` แต่ผ่าน injected loader |
| locale file `osc/l10n/*.json` | 28 (ตามรายงาน 24 ต.ค.) | รายงาน `../tools/LANGUAGE-AUDIT-20261024.md` ระบุ 1,127 reference keys และ fallback ภาษาอังกฤษ |

## 3) Native Angular + Material

- Angular root: `<html ng-app="main" ng-csp ng-strict-di>` และ `<div ui-view>` ใน `index.html`
- `app.js` ลงทะเบียน Angular module `main` และ dependencies รวม `ngMaterial`, `ngResource`, `ui.router`, `ngAnimate`, `pascalprecht.translate`
- การ Inject dependency ต้องใช้ **array annotation** เพราะเปิด `ng-strict-di`; เพิ่ม Route ผ่าน `angular.module('main').config(['$stateProvider', ...])` ก่อน bootstrap
- `vendor.js` ใช้ AngularJS 1.5.5 / AngularJS Material 1.1.3; ไม่ใช่ Angular ปัจจุบัน / `mat-*`
- Theme ต้นฉบับประกาศ Material `nvPrimaryPalette`, `nvAccentPalette`, `nvBackgroundPalette` และ `theme('default')...dark()` ใน `app.js`
- Material component modules 42: autocomplete, backdrop, bottomSheet, button, card, checkbox, chips, colors, content, datepicker, dialog, divider, fabActions, fabShared, fabSpeedDial, fabToolbar, gridList, icon, input, list, menu, menuBar, navBar, panel, progressCircular, progressLinear, radioButton, select, showHide, sidenav, slider, sticky, subheader, swipe, switch, tabs, toast, toolbar, tooltip, truncate, virtualRepeat, whiteframe
- Native `md-*` ที่พบใน `app.js`: `md-autocomplete`, `md-button`, `md-card`, `md-card-footer`, `md-content`, `md-divider`, `md-icon`, `md-input-container`, `md-item-template`, `md-menu`, `md-menu-bar`, `md-menu-content`, `md-menu-divider`, `md-menu-item`, `md-not-found`, `md-option`, `md-progress-circular`, `md-progress-linear`, `md-radio-button`, `md-radio-group`, `md-select`, `md-switch`, `md-toolbar`, `md-tooltip`, `md-virtual-repeat-container` (นับรวม 25 รายการจากการไล่แท็ก)
- Mod ใช้เพิ่มเติม: `md-tabs`, `md-tab`, `md-slider` และบาง template ใช้ `md-checkbox`; แยกคำว่า *implementation present* ออกจาก *runtime verified*
- ตัวอย่างที่ใช้จริง: `mods/osc-hook-settings.js` (select+options), `mods/osc-background.js` (switch+slider), `mods/gpu-status-panel.js` (tabs), `app.js` (main menu/md-menu)

## 4) NVIDIA Custom Directives — รายการจาก `app.js`

**Shell / Control:** `nvBase`, `nvMain`, `nvMainMenu`, `nvOscTopBar`, `nvOscTile`, `nvChevron`, `nvAccordion`, `nvAccordionPane`, `nvSlider`, `nvVirtualGridList`, `nvVirtualGridListItem`, `nvVirtualGridListRepeat`.

**Native Settings:** `nvPreferencesMenu`, `nvPreferencesAudio`, `nvPreferencesBroadcast`, `nvPreferencesConnect`, `nvPreferencesHangout`, `nvPreferencesHighlights`, `nvPreferencesKeyboardShortcuts`, `nvPreferencesMods`, `nvPreferencesNotifications`, `nvPreferencesOverlays`, `nvPreferencesPerformance`, `nvPreferencesPrivacyControl`, `nvPreferencesRecordings`, `nvPreferencesRecordingsFolderBrowser`, `nvPreferencesStream`, `nvPreferencesVideo`.

**Gallery / Video:** `nvGalleryFilesMenu`, `nvGalleryFilterMenu`, `nvGalleryHistoryMenu`, `nvGalleryRemoveMenu`, `nvGalleryUploadMenu`, `nvImageEditor`, `nvVideoEditor`, `nvVideoGifEditor`, `destinationPicker`.

**Game / Broadcast / Streaming:** `nvBroadcastMenu`, `nvCameraMenu`, `nvMicrophoneMenu`, `nvFilterControl`, `nvFilterEditbox`, `nvFilterSlider`, `nvOauthMenu`, `nvOauthDialogue`, `nvCoplayGuestControls`, `nvCoplayInvite`, `nvOverlay`, `nvWebrtcChat`.

**HUD / Feedback:** `nvOscNotifier`, `nvOsd`, `nvOsdComments`, `nvOsdPerfStats`, `nvOsdViewerCount`, `nvOsdWebcam`, `nvProgressIndicator`, `nvConfirmation`, `nvErrorDialog`, `nvShadowplayStatus`.

**Keyboard/Focus/Helpers:** `focus`, `focus1`, `focusOn`, `focusOnly`, `hoverFocus`, `hoverFocusGallery`, `getCustomizeWidth`, `nvFallbackSrc`, `imageonload`, `mdMenu` (NVIDIA override), `settingsCustomize`, `modsMenu`, `octoolMenu`, `edgeDevKit`.

> ชื่อด้านบนคือ **directive registration** ไม่ได้หมายความว่าวางแต่ละแท็กนอก Parent แล้วใช้ได้: เปิด `app.js` เพื่อดู `restrict`, `require`, template, scope, controller, service และ event contracts ก่อนนำไปใช้

## 5) แผนผัง Native UI-Router (38 states)

**Root/Main:** `base` (URL `/base`, idle), `main` (abstract, template `<nv-main>`), `main.main-menu` (`/main-menu`, `nv-main-menu`), `main.error-dialog`, `main.confirmation`, `main.guest-controls`, `main.coplay-invite`, `main.edge`.

**Settings:** `main.preferences` (`/preferences`, `nv-preferences-menu`); ลูกหน้าเดิม `main.preferences.connect`, `hangout`, `overlays`, `keyboard-shortcuts`, `recordings`, `recordings.folder-browser`, `stream`, `mods`, `broadcast`, `privacy-control`, `highlights`, `notifications`, `audio`, `video`, `perfsettings`.

**Gallery / Others:** `main.gallery`, `main.gallery.files`, `main.gallery.history`, `main.gallery.remove`, `main.gallery.upload`, `main.content`, `main.history`, `main.myrig`, `main.microphone`, `main.oauth-menu`, `main.broadcast-menu`, `mods`, `nvcamera`, `octoolmenu`.

**6 Mods States ที่ตรวจพบ:**

| State | URL | Source | UI shell |
|---|---|---|---|
| `main.customize-main-menu` | `/preferences/customize-main-menu` | `mods/main-menu-live.js` | onEnter redirect ไป native menu + editor; ไม่ใช่หน้า Settings ทั่วไป |
| `main.gpu-profiles` | `/preferences/game-profiles` | `mods/gpu-profiles-route.js` | placeholder route + JS enhancement/guard |
| `main.osc-capture-advanced` | `/preferences/video-capture/advanced` | `mods/advanced-capture-route.js` | native-style Settings + md-select |
| `main.osc-fps` | ตรวจ `mods/osc-fps.js` | `mods/osc-fps.js` | native Settings shell |
| `main.osc-hook` | `/preferences/overlay-hook` | `mods/osc-hook-settings.js` | native Settings shell |
| `main.osc-renderer` | `/preferences/overlay-renderer` | `mods/osc-renderer.js` | native Settings shell |

**ห้ามใช้ชื่อ state ว่าลูกของ `main.preferences` แล้วอนุมานว่า Angular UI-Router เป็น state parent ของมันโดยอัตโนมัติ:** ใน `app.js` หลาย state ตั้ง `parent:"main"` ชัดเจน ชื่อเป็น convention และ URL อาจเป็น absolute/custom path; เปิด State declaration จริงก่อนใช้ relative route/Back.

## 6) โครงสร้าง DOM / NVIDIA Visual Design System ของเดิม

```text
<nv-main>
  <nv-osc-top-bar>                  # title / close button / hotkeys
  <div ui-view>
    <nv-main-menu>                  # Native main menu: md-toolbar > md-menu-bar > md-menu > button.menu-button
    หรือ <nv-preferences-menu>      # Settings index
    หรือ Settings sub-page:
      <div class="osc-content" layout="row">
        <div class="osc-bread-crumb"><nv-osc-tile ...></nv-osc-tile></div>
        <div class="osc-central-div" layout="column">
          <div class="preferences-root-panel"> ... Material controls ... </div>
        </div>
        <div class="osc-side-buttons" layout="column">
          <button class="osc-button osc-button-green">Apply</button>
          <button class="osc-button osc-button-gray">Back</button>
        </div>
      </div>
```

**ต้นฉบับ CSS ใน `app.js` (ไม่ใช่สัญญาว่า Runtime computed style ยังค่านี้):**
- `.osc-content` มี `padding-top:80px` และพื้นหลังโปร่งใส
- `.osc-bread-crumb` เดิม `240×240px`, `margin-right:40px`, พื้นหลังดำ
- `.osc-central-div` มีเส้นบน **6px `#76b900`**, พื้นหลัง `#262b2f`, min-width 450px
- `.preferences-root-panel` เดิม `margin:40px`, `min/max-width:600px` และ `min/max-height:696px`
- `.osc-side-buttons` มี `padding-left:40px`; `.osc-button` เดิม `240×80px`, `margin-bottom:20px`, `font-size:20px`
- `.osc-button-green` พื้นหลัง `#76b900` (focus border white), `.osc-button-gray` `#32363b` (focus border green)
- `.main-menu-container` padding ซ้าย/ขวา 40px; native `md-menu` base เดิม `240×240px`; layout V2/V4/V5 อาจ reflow ตาม owner
- `body,html` พื้นหลังโปร่งใส, size 100%, overflow hidden; CEF OSR renders alpha/compositor
- **ห้ามใช้ค่าตายตัวพวกนี้แก้ทั้ง Global** เพราะ `mods/base.css`, `mods/preferences.css` และ route CSS มี override; ให้ตรวจ computed style ของ Build/CEF
- ตัวอย่าง CSS override ที่ต้องระวัง: `mods/base.css` บังคับ `.osc-central-div{width:100%!important}` และ layout padding, `mods/preferences.css` บังคับ 380px ในบาง route, `mods/gpu-profiles.js` แทรก CSS geometry ให้ Main-menu แบบ runtime

## 7) Icons, Fonts, Theme, Motion

**Icons:** `app.js` ประกาศ `@font-face{font-family:nvgcshare;src:...nvgcshare.woff2}` และ class `.share-icon`. พบ mapping `.icon-*:before` 88 ชุด (รวม `icon-settings`=`\\e900`, `icon-record`=`\\e933`, `icon-replay`=`\\e935`, `icon-camera`=`\\e908`, `icon-chevron_down`=`\\e909`, `icon-app_close`=`\\e91a`); `icon-performance` ชี้ image asset ต่างจาก glyph.

**รายการ icon class ใน Source ที่ใช้บ่อย:** `icon-settings`, `icon-preferences`, `icon-record`, `icon-replay`, `icon-stream`, `icon-broadcast`, `icon-screenshot`, `icon-gallery`, `icon-camera`, `icon-mic_off`, `icon-mic_on`, `icon-performance`, `icon-freestyle`, `icon-edit_tune`, `icon-chevron_left`, `icon-chevron_right`, `icon-chevron_up`, `icon-chevron_down`, `icon-arrow_back`, `icon-app_close`, `icon-video_capture`, `icon-pref_overlays`, `icon-pref_recordings`, `icon-pref_notifications`. อย่าใช้ Unicode/emoji ทดแทน Icon เดิมเมื่อมี Glyph รองรับ.

**ไทย:** `osc/MiSansThai.ttf` + `mods/osc-thai-font.css`; ห้ามตั้ง global font ทับ icon font `nvgcshare`. **Material Theme** มาจาก Angular material palette ของ NVIDIA ไม่ใช่สีของ CSS Mods ทุกหน้า.

**Animation:** `mods/osc-motion.js`/`osc-motion-engine.js`/`osc-motion-timing.js` ควบคุม open/close, background, top, menu, phase; `mods/osc-background.js` + `osc-bg-webgl.js` ทำ BG, ต้อง exclude Game Filter/Photo Mode และไม่ทำ backdrop ค้างเมื่อ navigate กลับ; `mods/nv-next-v5-native.js` และ `nv-freestyle-editor.js` จัดการ native tile geometry และ drag/input. ระวัง RAF/MutationObserver/timeout ซ้อนกันและ `prefers-reduced-motion`.

## 8) วิธีโหลด Mods สองช่องทาง (เป็นจุดวิกฤต)

1. **HTML Bootstrap (`osc/index.html`):** ลงทะเบียน JS/CSS ส่วนหลักโดยตรง; Custom Angular States อย่าง `osc-hook-settings.js`, `osc-renderer.js`, `osc-fps.js` และ `advanced-capture-route.js` โหลด HEAD ก่อน Angular bootstrap; หมายเหตุว่าไฟล์ที่วางเฉย ๆ ใน `mods` **ไม่ถูกโหลดอัตโนมัติ**
2. **Runtime native injection:** `NVIDIA OSC/src/osc_main.cpp` inject JavaScript ที่เรียก `fetch('/mods/list',{cache:'no-store'})`; ตรวจทุก ~2000ms; ใส่ `data-nvmod`/`data-nvversion`, load script/link ตาม version และลบ element ที่ไม่อยู่ใน allowlist; ทำงานหลัง page runtime เริ่ม
3. **Backend whitelist:** `NVIDIA NodeAPI/index.js` อนุญาตเฉพาะ **18 assets**: `ansel-game-filter.js`, `base.css`, `folder-browser-navigation.js`, `gallery-media-source.js`, `gpu-profiles.js`, `gpu-profiles.css`, `gpu-status-panel.css`, `gpu-status-panel.js`, `notification-settings.js`, `overlays.css`, `preferences.css`, `privacy-control.css`, `reshade-manager.css`, `reshade-manager.js`, `providers.css`, `notifications.css`, `video-capture-options.css`, `video-capture-options.js`
4. **Double-load hazard:** `ansel-game-filter.js` และ `gallery-media-source.js` ปรากฏทั้ง direct HTML และ allowlist; Native dynamic loader หาเฉพาะ `[data-nvmod]` ไม่รู้ว่ามี script ที่โหลดผ่าน HTML ไปก่อนหรือไม่: **ต้องตรวจ idempotency guard ในไฟล์ และทดสอบ CEF** ก่อนสรุปว่าซ้ำจริง/ไม่ซ้ำจริง
5. **Dynamic reload hazard:** การเปลี่ยน version อาจ re-run top-level JS / install listeners/Observers อีกครั้ง; Angular state/config ที่ลงทะเบียนหลัง bootstrap ไม่ได้ guaranteed ว่าใช้ได้ ถ้าไม่ได้ใส่ bootstrap route โดยตรง; CSS ordering/SPECIFICITY เปลี่ยนเมื่อ loader append ใหม่
6. **เพิ่ม UI ใหม่ให้ครบ 3 จุดตามกรณี:** Source JS/CSS + Loading strategy (index หรือ whitelist ตามชนิด) + route/menu owner/handler (ไม่ใช่แค่สร้างไฟล์). ต้องตรวจ `Project -> Build -> live served asset -> actual CEF`

## 9) Mods Ownership Matrix / จุดเปิด UI

| Owner / Module | หน้าที่ UI | ข้อควรระวัง |
|---|---|---|
| `main-menu-live.js/css` | ปรับ native menu/layout V2, Customize redirect/editor | native click/focus และ ownership |
| `main-menu-unified-model.js` + `main-menu-unified-native.js/css` | V4 layout/model/native reflow | อย่ารันพร้อม V5 เป็น geometry owner |
| `nv-layout-v5-core.js` + `nv-next-v5-native.js/css` | V5 pure model + native md-menu DOM owner | LocalStorage flag `DulukaNextNativeLayoutV5Enabled`, ไม่ถือว่า prod on โดยอัตโนมัติ |
| `nv-freestyle-editor.js/css` + `nv-freestyle-settings.js/css` | V5 edit transaction + original Settings panels | Escape/Material popup/drag ownership, Cancel/Done |
| `main-menu-group-editor.js/css` + `main-menu-group-model.js` | Legacy Group A editor | opt-in/legacy; ไม่ย้ายกลับมาแทน V5 |
| `gpu-profiles-route.js` + `gpu-profiles.js/css` | Game Profiles entry, readiness, GPU status/profile UI | placeholder state + DOM inject, CSS geometry global |
| `gpu-status-panel.js/css` | Game Filter / Photo mode tabs / GPU status | child Angular $compile, CSS ซ่อน siblings |
| `reshade-manager.js/css` | ReShade Manager subpage/installer handoff | DOM show/hide, tabs, md-select/checkbox, CEF focus |
| `video-capture-options.js/css` | Video settings/codec/menu enhancement | panel fixed sizing, available options ต้องยึด backend capability |
| `advanced-capture-route.js/css` | Advanced Capture native Settings route | design preview แยก live encoder/preset; disabled capability |
| `osc-hook-settings.js` | Hook CPU/GPU per API Settings | inline scroll 76vh; status ≠ drawing proof |
| `osc-renderer.js` | CEF/Host Renderer Settings | request vs observed GPU, async rollback |
| `osc-fps.js` | FPS Settings | Back/entry origin |
| `osc-background.js/css`, `osc-bg-webgl.js` | backgrounds/motion/particles | Game Filter exclusion, GPU/Canvas fallback |
| `osc-motion.js/css`, `osc-motion-engine.js`, `osc-motion-timing.js` | Open/close motion / positions / RAF | route/layout lifecycle, no premature native click |
| `osc-ui-localization.js`, `osc-ui-phrase-map.js`, `launcher-language-sync.js`, `osc-thai-font.css` | i18n / Thai / Launcher→OSC | fallback ≠ genuine translation |
| `notification-settings.js`, `notifications.css`, `notifier/*` | notifier/OBS panel/toasts | separate toast owner, z-index, lifetime |
| `gallery-media-source.js`, `folder-browser-navigation.js` | Gallery video/browser patches | MutationObserver lifecycle, media seek |
| `hotkey-live.js`, `osc-renderer.js` | bridge/actions/settings | duplicate bind, Alt+Z/ESC interaction |
| `ansel-game-filter.js`, `nvcamera-test-mode.js` | Photo mode/Game Filter integration | preserve native shell and feature gates |

## 10) CSS / Override Atlas (20 top-level CSS)

| CSS owner | `!important` จำนวนพบ | หมายเหตุ |
|---|---:|---|
| `gpu-profiles.css` | 181 | specificity/geometry hotspot |
| `nv-freestyle-settings.css` | 124 | editor layout hotspot |
| `base.css` | 57 | Global / Native override |
| `main-menu-unified-native.css` | 55 | old V4 menu geometry |
| `preferences.css` | 29 | page-specific width 380px |
| `osc-motion.css` | 27 | Motion + native geometry |
| `notifications.css` | 24 | toasts |
| `nv-next-v5-native.css` | 23 | V5 scoped geometry |
| `osc-background.css` | 22 | BG + motion settings |
| `reshade-manager.css` | 20 | hidden tab/overflow |
| `main-menu-live.css` | 19 | V2 native menu/edit |
| `main-menu-group-editor.css` | 15 | legacy editor |
| `nv-freestyle-editor.css` | 7 | V5 edit |
| `advanced-capture-route.css` | 4 | scoped route sizing |
| `overlays.css` | 4 | OSD Settings |
| `providers.css` | 4 | provider settings |
| `gpu-status-panel.css` | 1 | filter settings |
| `osc-thai-font.css` | 1 | text fonts |
| `privacy-control.css` | 1 | privacy |
| `video-capture-options.css` | 0 | still has fixed geometry in px |

**จำนวน `!important` เป็นตัวบ่งชี้ความเสี่ยง ไม่ใช่ข้อพิสูจน์ว่า CSS ผิด.** ก่อนแก้ ต้องดู calculated/computed style, route owner และ cascade order จริงใน CEF. ไม่ลบ override แบบเหมา.

## 11) Interaction / Bridge / Lifecycle Contract

- **Menu click:** Native `md-menu > button.menu-button` ใช้ `mainMenu.openMenu(...)`/tile handlers; Customize ไม่ควร dispatch Record/Stream ขณะ drag; controls ต้องเชื่อม action จริงและ Identity เดิม
- **Navigation:** `$state.go('main.xxx')`, ผ่าน Angular UI-Router; Native main/default `/base`, Settings `main.preferences`, subpage; Back จำ parent entry (จาก Main-menu หรือ Settings) และ restore focus
- **Focus:** NVIDIA `hoverFocus` ดัก mouseenter→focus / mouseleave→blur; มี `focusOnly` และ keyboard handlers; ห้ามใส่ hover-focus ทุก control โดยไม่ตรวจ material popup/keyboard behavior
- **Escape:** Original app/OSC hotkeys สามารถปิด OSC; Material popup ต้องได้ dismiss ก่อน เช่น guard ใน `nv-freestyle-editor.js`; ตรวจ ESC เมื่อ md-select เปิด, Cancel editor และ route exit
- **Backend:** `window.cefQuery` native message router หรือ shim HTTP `/cefquery`; submit only supported commands; validate response, busy/disabled, rollback when save fails; payload/action ต้องอิง API ของจริง
- **Dispose:** `$scope.$on('$destroy')`, `MutationObserver.disconnect()`, clearTimeout/Interval/RAF, unregister DOM/global listeners; Dynamic loader reload ต้อง idempotent
- **Localization:** Angular Translate `translate="l10n.key"`; 28 locales, English fallback; phrase map ครอบคลุม legacy hard-coded strings บางส่วน; `osc-ui-localization.js` ใช้ MutationObserver; launcher sync เป็น Launcher→OSC ไม่ใช่ two-way
- **Input/FPS/UI:** Native Host `GetViewRect` และ `ForwardKey` คุม viewport/input; GPU/CPU frame path เป็นอีกชั้นจาก HTML; DOM animate ดูลื่นบน Desktop ไม่พิสูจน์ DX9 Hook overlay FPS

## 12) Risk Register / QA Gate

| Priority | Issue / Owner | หลักฐานที่พบ | ต้องพิสูจน์เพิ่ม |
|---|---|---|---|
| P0 | Double-loaded JS / Hot reload | `index.html` + `/mods/list` + host 2s loader | idempotency, repeated listeners, CEF reload |
| P0 | V2/V4/V5 Geometry owner | native md-menu reflow, `gpu-profiles.js` runtime global positioning | single owner after route and animation, no flicker |
| P0 | Angular bootstrap timing | routes loaded direct in HEAD vs async mods injection | state registration/open/Back without stale injector |
| P1 | Global width / cascade | `base.css` `.osc-central-div width:100%!important`, route CSS and 181 override in GPU | Content not behind side buttons; avoid blind CSS overrides |
| P1 | Main-menu return | Motion/Editor/Settings route transitions | menu geometry correct first frame, focus back, backdrop no leak |
| P1 | Popup/ESC | `md-select` and native OSC Escape handler | popup closes not entire OSC unexpectedly |
| P1 | ReShade/Game Filter | DOM injection, hide/show tabs, child $compile | Back, accessibility, no duplicate scope |
| P1 | Video/Advanced Capture | preview vs live settings, disabled catalog | CEF viewport fit, save truthful, no false capability |
| P1 | Hook/Renderer | unsupported GPU routes, live state indicators | cannot select unsupported, observed ≠ requested |
| P2 | Language / icon / font | legacy literal labels, English fallback, nvgcshare | Thai/English, glyph intact, no text overflow |
| P2 | MutationObserver/Timers | Gallery, Profile, Notifier, Motion, localization | teardown, no resource leaks across reopen |

รายงานก่อนหน้าที่เกี่ยวข้อง: `mods/OSC-UI-COMPLIANCE-AUDIT-20261024.md` เคยรัน 205 tests / **200 passed, 5 failed** ใน snapshot 24 ต.ค. (1 Freestyle test stale contract และ 4 Customize mismatches) — **ตัวเลขนี้เป็นผลในอดีต ไม่ใช่ผลเทสต์รอบปัจจุบัน**. ต้องรันใหม่เมื่อจะรับรอง CEF/UI

## 13) ขั้นตอนมาตรฐานสำหรับงาน UI ต่อจากนี้ (บังคับ)

1. อ่าน `mods/อ่านก่อนทำ UI.md` + เอกสารนี้; หา owner ของหน้า Route / Directive / injected panel ให้ชัด
2. ตรวจว่า asset ถูกโหลดช่องทางไหน: `index.html` / `/mods/list` / dynamic DOM. ตรวจ repeated loads และ Angular bootstrap order
3. ใช้ NVIDIA native shell, md-/nv- native controls, icon glyph, theme, layout, original click/focus/Back + l10n
4. ตรวจ state change / native DOM geometry single owner / md-popup / ESC / Alt+Z / Motion / Background
5. ห้ามใช้ global CSS/fixed md sizing หรือ scroll ซ่อนปัญหา; ตรวจ source CSS cascade และ viewport CEF จริง
6. ฟีเจอร์ที่ Backend ไม่ยืนยันต้อง disabled; preview-only ต้องประกาศชัดและห้าม SAVE เป็นค่าจริง
7. ทดสอบ source JS syntax, relevant Node tests, locale key coverage, Project→Build asset parity ก่อนเปิด CEF
8. CEF acceptance: Home → Settings → subpage → Back → Main → Customize → Done/Cancel → popup → close/open; ดู screenshot/computed geometry/console/error/repeated listeners ใน Runtime จริง
9. แจ้งว่า Source ผ่านอะไร, Live CEF ผ่านอะไร, Deploy หรือไม่ และติดปัญหาอะไรแบบแยกชั้น

## 14) คำสั่งตรวจแบบ Read-only

```powershell
$ui = 'C:\My Project\NVIDIA-Shadowplay\Project\Overlay On-Screen Control\NVIDIA NodeAPI\osc'
# Source inventory
Get-ChildItem "$ui\mods" -File | Sort-Object Name | Select-Object Name,Length
# Syntax-check top-level UI mods
Get-ChildItem "$ui\mods" -Filter '*.js' -File | ForEach-Object { node --check $_.FullName }
# Native routes and UI bindings
Select-String -LiteralPath "$ui\mods\osc-hook-settings.js" -Pattern '\$stateProvider.state','osc-content','md-select'
# Locale keys (read-only audit; do not use --fix without explicit task)
node 'C:\My Project\NVIDIA-Shadowplay\Project\Overlay On-Screen Control\NVIDIA NodeAPI\tools\locale-coverage.cjs'
```

**ข้อจำกัด:** Static inspection + JS syntax **ไม่ใช่** visual QA หรือ proof ว่า Native OSC/Hook renders stable. หากต้องแก้ Native Host input/compositor ให้ตรวจ `NVIDIA OSC/README.md` และ `src/osc_main.cpp` แยกจากงาน Web UI.

## 15) แหล่ง Source ที่ยึดตรวจ

- `osc/index.html`, `osc/vendor.js`, `osc/common.js`, `osc/app.js`, `osc/config.js`, `osc/user-config.js`, `osc/cefquery-shim.js`, `osc/manifest.json`
- `osc/mods/*.js` และ `osc/mods/*.css` (top-level) + `osc/mods/notifier/`
- `NVIDIA NodeAPI/index.js`: `GET /mods/list` allowlist
- `NVIDIA OSC/src/osc_main.cpp`: CEF UI Host / injection / native bridge / input / OSR
- `NVIDIA NodeAPI/tools/LANGUAGE-AUDIT-20261024.md`, `osc/mods/OSC-UI-COMPLIANCE-AUDIT-20261024.md` (historical QA snapshot)
- `osc/README.md`, `NVIDIA OSC/README.md`, `NVIDIA NodeAPI/tests/`

> **ห้ามใช้การอ่านเอกสารนี้แทนการดู Source ปัจจุบัน**. ถ้า Source มี Agent อื่นเปลี่ยน ให้ตรวจ diff และอัปเดต Atlas นี้เฉพาะส่วนที่พิสูจน์ได้
