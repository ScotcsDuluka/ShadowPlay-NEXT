# ShadowPlay NEXT — Audit: NVIDIA OSC injected UI compliance
วันที่ 24 ตุลาคม 2026 · เจ้าของการตรวจ: Overlay On-Screen Control / NVIDIA NodeAPI

> **อ่าน `osc/mods/อ่านก่อนทำ UI.md` แล้ว** ครบ 195 บรรทัด และใช้เป็นเกณฑ์ในการตรวจครั้งนี้
>
> **ขอบเขต:** ตรวจ Static Source + Build parity + Node.js syntax + UI regression. **ยังไม่ได้**ทดสอบคลิก/Focus/Overflow/Animation บน CEF จริง ไม่ถือว่า Live UI ผ่านทั้งหมด
>
> **ลักษณะงาน:** Audit-only — ไม่แก้ JS/CSS/Route ของ Agent อื่น ไม่ Deploy และไม่ Restart เนื่องจากการเขียนรายงานอย่างเดียวไม่จำเป็นต้อง Restart

## 1. สรุปผลและตัวเลขยืนยัน

- `osc/mods/` พบไฟล์ JS ระดับบนสุด **32 ไฟล์**, CSS ระดับบนสุด **20 ไฟล์**, รวม JS/CSS/HTML ทั้ง subtree **57 ไฟล์** (รวม `notifier/`)
- ตรวจ `node --check` ทุก JS ระดับบนสุด: **32/32 ผ่าน**
- Source พบ **6 UI-Router States** ใน mods ไม่มีชื่อ state ซ้ำ:
  - `main.customize-main-menu` — `main-menu-live.js`
  - `main.gpu-profiles` — `gpu-profiles-route.js`
  - `main.osc-capture-advanced` — `advanced-capture-route.js`
  - `main.osc-fps` — `osc-fps.js`
  - `main.osc-hook` — `osc-hook-settings.js`
  - `main.osc-renderer` — `osc-renderer.js`
- Assets ทั้ง JS/CSS ที่อ้างจาก `osc/index.html` มีทั้ง Source และ Build และ **SHA-256 ตรงกันทั้งหมด** ณ เวลาตรวจ
- `osc/index.html` Source/Build ตรงกัน
- `GET /mods/list` Runtime ส่งรายการ Source-owned mods 18 รายการ มี ReShade/GPU/Video Capture ตามที่คาด
- `GET /Backend/v.1.0/health`: HTTP **200**
- ชุด UI/GPU/Capture/Localization/Motion/Customize ที่เกี่ยวข้อง 23 test files: **205 tests, ผ่าน 200, ไม่ผ่าน 5**
  - `freestyleSettingsUi.test.js` หนึ่ง failure **เป็น Test ล้าสมัย**: ยังบังคับให้ปุ่ม Install ReShade เป็น disabled placeholder ทั้งที่ฟีเจอร์ถูกเปลี่ยนเป็นปุ่ม Open ReShade Manager แล้ว ต้องปรับ assertion ให้ยืนยันการเปิด Manager, API safety และ contract ใหม่
  - `mainMenuCustomize.test.js` อีก **4 failures** เกี่ยวกับ Settings icon/label ใน initial NVIDIA template, `icon-app_close`, Controls tray collapse (`icon-chevron_down`) และ locale label `Settings` vs `การตั้งค่า`. ถือเป็น **contract mismatch / possible regression** ต้องเทียบ Owner V5 ปัจจุบันและตรวจ CEF ก่อนแก้ Test หรือ UI; ห้ามสรุปว่าเป็นแค่ Test เก่าโดยไม่มีหลักฐาน

## 2. Audit หน้า / แผง ที่ลงทะเบียนเป็น State หรือฉีดเข้า Native Settings

| Surface / Owner | การเชื่อม UI | สถานะ | ผลตรวจ / ประเด็นที่ต้องทำ |
|---|---|---|---|
| Overlay Renderer — `osc-renderer.js` | `main.osc-renderer` + `osc-content`, `nv-osc-tile`, `preferences-root-panel`, `osc-side-buttons`, `md-select`, Angular controller | **ตรงโครงสร้าง** | มี disabled/busy/error และ $destroy; CEF Popup/Back/render ยังไม่ทดสอบ |
| Overlay FPS — `osc-fps.js` | `main.osc-fps` + Native Settings shell, `md-select`, status/rollback | **ตรงโครงสร้าง** | Back hardcoded `main.main-menu`; ต้องยืนยัน origin/return-state เมื่อเข้าจาก Settings ตาม §8.3 |
| Hook Settings — `osc-hook-settings.js` | `main.osc-hook` + Native shell และ `md-select` | **ต้องปรับ Layout** | Template มี `style="max-height:76vh;overflow-y:auto;overflow-x:hidden"` บน `osc-central-div` ซึ่งใช้ Scroll/height ฝืนโดยตรง; คู่มือ §4 ให้จัด Content Region ก่อน |
| Advanced Capture — `advanced-capture-route.js` | `main.osc-capture-advanced` + Native shell, `md-input-container`, `md-select`, `osc-side-buttons`, l10n | **ตรงโครงสร้าง** | ตัวเลือกที่ยังไม่ได้ Implement มี disabled; ต้องทดสอบ grid ที่ viewport CEF และ confirm backend capability |
| Game Profiles route — `gpu-profiles-route.js` + `gpu-profiles.js` | State เป็น placeholder แล้ว JS สร้าง `osc-content`, `osc-central-div`, `preferences-root-panel`, side buttons | **ผสม Native + DOM injection** | มี native shell แต่ไม่ใช่ state template/controller เดียวแบบคู่มือ; ตรวจ lifecycle/Back/readiness/focus เมื่อ reopen |
| Game Filter — `gpu-status-panel.js` + `gpu-status-panel.css` | ฉีด `#nv-filter-settings` ลง Native Preferences และใช้ Angular `$compile`, `md-tabs`, `md-select`, `md-switch` | **ใช้ Native controls ถูก แต่ CSS เสี่ยง** | `.nv-filter-settings-host > :not(#nv-filter-settings) {display:none!important}` ซ่อน Native siblings; `.nv-freestyle-select` fixed 300px / 230px; พิจารณาจัด flex/container แทน |
| ReShade Manager — `reshade-manager.js/.css` | ฉีด Subpage ลง Game Filter Settings, มี `md-input-container`, `md-select`, `md-checkbox` และ Angular `$compile` + child scope | **ดีขึ้น แต่ยังไม่ตรง Submenu contract** | ยังไม่ใช่ `$stateProvider.state` ของ Submenu, Back ผ่าน DOM show/hide; CSS ซ่อน Tabs และใช้ `max-height:calc(100vh - 164px);overflow-y:auto`. ปรับไปใช้ Native Settings/Submenu owner ตาม §8 หากต้องการหน้าเต็ม; ทดสอบ CEF material popup, checkbox/focus ก่อนรับรอง |
| Video Capture — `video-capture-options.js/.css` | เสริมหน้า Settings ด้วยแผงที่สร้างด้วย JS, ใช้ NVIDIA labels/buttons และ runtime catalog | **ต้องปรับ Layout** | CSS บังคับโครง `flex:0 0 471px`, ปุ่ม `width/min-width:153px;height:34px`, min-width field 190px และ assumptions 960px CEF; เสี่ยง overflow, ให้ container/flex จัดตามเนื้อหา (ไม่ใช่ตั้งขนาด `md-*` เหมา) |
| Notifier / OBS Settings — `notification-settings.js` | เพิ่ม native-like settings rows และ `md-select` ที่ compile กับ scope; status/busy/timer | **พอใช้ / ตรวจ Lifecycle** | DOM injection แทน dedicated State เป็น in-page enhancement; ตรวจ cleanup ของ interval, popup/focus, route switching, locale |
| Performance / GPU panels — `gpu-profiles.js`, `gpu-status-panel.js` | Native elements, dynamic runtime status, `osc-button` | **พอใช้แต่เสี่ยง geometry** | ตรวจ OS-selected GPU vs observed GPU state, DOM owner ซ้อนกับ legacy main-menu code; ไม่ใช้ fixed geometry เป็นทางลัด |
| Freestyle Settings / Editor — `nv-freestyle-settings.js`, `nv-freestyle-editor.js`, `ansel-game-filter.js` | ใช้ `md-*` ร่วมกับ Native Game Filter และ dynamic DOM/event handling | **ต้อง CEF acceptance** | เสี่ยง lifecycle ของ Observer และ CSS layering; ห้ามแก้ Engine/Presets ใน Audit นี้ |
| Background / Motion Settings — `osc-background.js`, `osc-motion.js`, `osc-motion-engine.js` | Native `md-switch`/`md-slider` และ overlay background | **พอใช้ / ต้องตรวจการกลับหน้า** | ตรวจว่าพื้นหลังไม่ติดใน Game Filter, Return-to-Main-menu, input/focus หลัง motion; ปัจจุบันมี route-exclusion guards |
| Notification surfaces — `notifier/notifier-system/notifier.js`, adapters | แผง Toast ซ้อน OSC ที่มี Owner แยก | **แยกจาก Settings route** | ตรวจ z-index, ปุ่ม, ข้อความไม่ทับ Content/Top bar, teardown/reconnect และภาษาใน CEF |
| Gallery Player — `gallery-media-source.js`, `folder-browser-navigation.js` | Dynamic `MutationObserver` กับ Native Gallery video/source | **ไม่ใช่ Settings page** | ตรวจ MSE seek/cleanup ใน CEF; script มีอยู่ทั้งใน `index.html` และ `/mods/list` — ตรวจ host loader จริงว่ามีการโหลดซ้ำหรือไม่ อย่าอนุมานจากรายการเฉย ๆ |

## 3. Main-menu, V5, Native Identity — จุดวิกฤต

- `main-menu-live.js`, `nv-layout-v5-core.js`, `nv-next-v5-native.js`, `main-menu-unified-model.js`, `main-menu-unified-native.js`: มีความพยายามรักษา Native `md-menu` และ Geometry Owner เดียวในโหมด V5 อย่างถูกทาง; **แต่ 4 Customize test contracts ไม่ผ่าน** จึงยังรับรอง Identity/Focus/Locale ตอน CEF Render ไม่ได้
- **เสี่ยงสูง:** `gpu-profiles.js` ฟังก์ชัน `ensureMainMenuTileStyle` สร้าง CSS แบบกว้าง:
  - `.main-menu-container{min-width:960px!important;}`
  - `.main-menu-container md-menu-bar{min-width:960px!important;}`
  - `md-menu` native tiles กำหนด `left/top/right/width/height` แบบ `!important`
  - §4 และ §8 ของคู่มือห้ามกำหนด geometry บังคับ Native main-menu นอก Layout Owner; ส่วนนี้อาจเป็น legacy fallback ของ V2/V4 จึงต้องตรวจ gating/feature flag ว่ารันเมื่อ V5 active หรือไม่ ก่อนลบ/แก้
- `main-menu-group-editor.js` (legacy opt-in): สร้าง `<select>`/ปุ่มเองผ่าน `innerHTML` และใช้ DOM events โดยตรง — ไม่ใช่ native `md-select`; หากยังเปิดใช้งานควรย้าย control ไป Angular template เมื่อปรับครั้งต่อไป; หาก V5 ปิด legacy ต้องไม่รีแอ็กติเวตไฟล์เก่า
- CSS จำนวน override ใน 20 ไฟล์: `gpu-profiles.css` **181** `!important`; `nv-freestyle-settings.css` **124**; `base.css` **57**; `main-menu-unified-native.css` **55**; `gpu-status-panel.css` มี scoped fixed width ที่ `md-select`. จำนวน override **ไม่พิสูจน์บั๊กด้วยตัวเอง** แต่เป็นเป้าหมายตรวจ specificity/Geometry owner เมื่อพบ overflow
- ความกังวลหลักตรงกับคู่มือ: อย่า clone/rebuild md-menu เดิม, อย่าฝืน size `md-*`, อย่าใช้ scroll กลบ Overflow, รักษา NVIDIA GPU/Main-menu/Settings identity

## 4. Localization / keyboard / loading

- หน้า State เช่น FPS/Renderer/Hook/Advanced Capture ใช้ `translate="l10n.*"` อย่างมีโครงสร้าง; ReShade, Main-menu legacy editor, Game Profiles/Notification Settings ยังมี literal English/Thai ใน JS — **ยังไม่ยืนยัน locale key coverage ครบ** ต้องผูกกับ `osc/l10n/` และ `osc-ui-localization.js`
- `hover-focus`, Angular `ng-disabled` และ child scope มีใช้หลายหน้า ไม่ใช่ทุก custom element; ต้องทดสอบ TAB/ESC/Enter/Alt+Z, md-select popup/overlay, screen reopen
- Bootstrap script จาก `osc/index.html` ตรงกับ Build (รวม Advanced Capture); `/mods/list` ลงทะเบียน 18 mod assets; ตรวจ CEF actual loader, double-init guard กับรายการที่ปรากฏทั้ง Script bootstrap และ Manifest
- UI syntax/HTTP/static parity **ไม่ได้พิสูจน์การแสดงผลบน CEF**, ขนาดจริง หรือว่าปุ่มกดทำงานครบ

## 5. จัดลำดับแก้ไข (ห้ามแก้หลาย Owner พร้อมกันโดยไม่ประสาน)

**P0 — Main-menu/Customize Native geometry & identity:** ประสาน V5 owner; ตรวจ 4 Test failures ที่ Settings icon/label, remove icon E91A, tray collapse E909, locale; แยก test outdated vs runtime bug และหยุด global geometry override ใน `gpu-profiles.js` เมื่อ V5 เป็น Owner

**P1 — Video Capture overflow:** Owner ของ Capture UI แก้ fixed 471px/153px/fixed assumptions ด้วย container layout และทำ CEF 100% zoom/viewport acceptance; ห้ามเปิด GPU options เกิน capability จริง

**P1 — ReShade/Freestyle Settings contract:** ใช้ native settings submenu state ถ้าหน้าเต็ม, ไม่ซ่อน sibling content แบบกว้าง, ลด min-height/forced scroll, สร้าง Enter/Back/ESC/focus acceptance, map locale; อัปเดต stale disabled-Install test เป็น Open Manager contract

**P1 — Hook Settings content region:** เปลี่ยน inline `max-height:76vh;overflow-y:auto` เป็น scoped adaptive content region และทดสอบ Side Buttons/Top bar

**P2 — Legacy editor / Notification / Localization:** เลิก raw select ที่ยัง active, ตรวจ duplicate dynamic loader, cleanup/timers และ locale keys; แยก opt-in legacy จาก V5

## 6. Acceptance Matrix สำหรับตรวจ CEF จริง

ต้องเปิด OSC Build ใหม่และทดสอบ: Main-menu normal → Settings → GPU Profiles → Back → Customize → Done/Cancel → Main-menu; Hook/Renderer/FPS → Back; Capture Settings → Advanced → Back; Game Filter → ReShade Manager → Inspect/Back; Notification → OBS Settings; Gallery video; OSC close/open; เปลี่ยน locale ไทย/อังกฤษ; ความละเอียด CEF ตามจริง; ตรวจ Overflow, แผงซ้อน, md-select popup, focus/keyboard, runtime truth, animations

**สถานะ:** Static / JS Syntax / Deployment parity checked. 205 related tests: 200 pass / 5 fail. CEF visual acceptance: **NOT TESTED**. No UI source changes or NodeAPI restart were performed in this audit.
