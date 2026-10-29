# อ่านก่อนทำ UI — ShadowPlay NEXT / NVIDIA OSC

> **บังคับสำหรับงาน UI ใน `osc/` และ `osc/mods/`** — อ่านไฟล์นี้ก่อนออกแบบ, สร้างหน้าใหม่, แก้ CSS/JS/HTML, ปรับขนาด/ตำแหน่ง, แก้ Overflow, ทำ Preview หรือ Deploy UI ทุกครั้ง
>
> ตรวจ Source ณ **24 ตุลาคม 2026** บน AORUS-DESKTOP: `vendor.js`, `app.js`, `index.html`, `mods/` (ไม่ใช่การยืนยันว่าทุก Component ผ่าน CEF Runtime แล้ว)

## 0. เริ่มงานอย่างไร (ห้ามข้าม)
1. อ่านไฟล์นี้ แล้วเปิด **Source ของหน้าที่จะแก้จริง** พร้อมไฟล์ CSS/JS, Controller, Route, Template, และ Tests ที่เกี่ยวข้อง ห้ามเดา Component จากชื่อ
2. ตรวจว่ามี NVIDIA native UI/Angular Material เดิมที่นำกลับมาใช้ได้หรือไม่ **ใช้ของเดิมก่อน** แทนการสร้าง Control เลียนแบบเอง
3. ระบุ Owner ของ UI และตรวจว่าแก้ `Project/` หรือ `Build/` อยู่: `Project/` คือ Source; ปรับ Source อย่างเดียว **ไม่เท่ากับ Deploy** ให้ CEF ที่รันอยู่
4. รักษาโครงสร้าง Settings/Main-menu, Native handlers, keyboard/focus/hover, Angular scope, l10n, และสถานะ API/Runtime จริง
5. ก่อนจบตรวจ Regression + CEF Rendering, ทดสอบไม่ล้น/ไม่ทับ/ไม่กระโดดเมื่อเปลี่ยนหน้า และระบุผลทดสอบตามจริง ห้ามอ้างว่าใช้งานจริงถ้าเช็กได้แค่ Source

## 1. Stack ที่ติดตั้งจริง
- `osc/index.html`: `ng-app="main"`, `ng-csp`, `ng-strict-di`; โหลด `vendor.js`, `common.js`, `app.js` และ Mods ก่อนแสดง `ui-view`
- `osc/vendor.js`: **AngularJS 1.5.5**, **AngularJS Material 1.1.3** (`ngMaterial` พร้อมโมดูล Material 42 กลุ่ม)
- `osc/app.js`: Native NVIDIA OSC Angular templates/controllers/directives
- `osc/mods/*.js`, `*.css`: ส่วนขยายโปรเจกต์ ShadowPlay NEXT
- **ห้ามใช้ API ของ Angular Material (`mat-*`) รุ่น Angular ใหม่**, React Component, หรือ Web Component เป็นของทดแทนโดยไม่ออกแบบ Integration แยกและอนุมัติ

## 2. Material Components — พบแท็กใน NVIDIA OSC app.js
| แท็ก | งานหลัก | หมายเหตุ |
|---|---|---|
| `md-button`, `md-icon` | ปุ่ม/ไอคอน | รักษา class, icon fonts และ focus ของ NVIDIA |
| `md-select`, `md-option` | Dropdown | ผูก `ng-model`, `ng-change`, `ng-disabled`, `aria-label`; ใช้ `osc-select` ตามจุดที่เหมาะ |
| `md-switch` | เปิด/ปิด | ผูก state จริง ไม่สลับภาพลวง |
| `md-radio-group`, `md-radio-button` | เลือกค่าเดียว | ใช้ `ng-model` |
| `md-input-container` | Form/label | จัด layout จาก Container |
| `md-content`, `md-toolbar`, `md-card`, `md-divider` | แผง/แถบ/การ์ด/เส้นคั่น | ให้ NVIDIA Styles เดิมมีสิทธิ์ทำงาน |
| `md-menu`, `md-menu-bar`, `md-menu-content`, `md-menu-item`, `md-menu-divider` | เมนู | Main-menu มี handler/geometry native: **อย่า clone/rebuild เมนูเพื่อแค่เปลี่ยนภาพ** |
| `md-tooltip` | คำอธิบาย hover | ระวัง focus/overlay |
| `md-progress-circular`, `md-progress-linear` | โหลด/สถานะ | ต้องผูกกับสถานะงานจริง |
| `md-autocomplete`, `md-virtual-repeat-container` | ค้นหา/รายการมาก | ต้องตรวจ scope และ CEF |

**พบใช้งานใน Mods:** `md-slider` (Background), `md-tabs`/`md-tab` (Game Filter), `md-switch`, `md-select` (Hook/Renderer/FPS/GPU); อ้างอิง `mods/osc-background.js`, `mods/gpu-status-panel.js`, `mods/osc-hook-settings.js`.
**มีใน `vendor.js` แต่ไม่ได้ยืนยันการใช้งานหน้าใหม่:** `md-checkbox`, `md-dialog`, `md-toast`, `md-datepicker`, `md-sidenav`, `md-chips`, `md-grid-list` และโมดูลอื่น: ทดสอบ API และ CEF ก่อนใช้งาน

## 3. NVIDIA Custom Directives/Styles — มีอยู่จริงใน app.js
- เมนู/หัวหน้า: `nv-main-menu`, `nv-osc-top-bar`, `nv-osc-tile`
- Settings: `nv-preferences-menu`, `nv-preferences-*` (เฉพาะหน้า; ตรวจ binding/controller ก่อนใช้)
- Controls: `nv-slider`, `nv-accordion`, `nv-accordion-pane`, `nv-filter-control`, `nv-filter-slider`, `nv-chevron`
- Dialog/feedback: `nv-confirmation`, `nv-error-dialog`, `nv-progress-indicator`
- ปุ่มข้าง Settings เดิม: `button.osc-button.osc-button-green` / `osc-button-gray` พร้อม `hover-focus` และ `ng-click`; **ไม่จำเป็นต้องแทนด้วย `md-button` ทุกจุด**
- **หมายเหตุ:** `nv-*` บางตัวผูกบริการ/Controller เฉพาะหน้า ไม่ใช่ drop-in ทั่วไป เปิด Source/directive และทดสอบก่อนนำมาใช้

## 4. Layout, Size และ Overflow — ข้อบังคับ
1. **ห้ามกำหนด width/height ตายตัวให้ `md-*` แบบเหมา** เพื่อแก้ปัญหาหน้าล้น; ให้จัดพื้นที่ด้วย Container, `layout`, `flex`, `min-width:0`, `box-sizing` และ spacing ที่สัมพันธ์กับหน้าจอ
2. ห้ามเขียน Global CSS ทับ `md-select`, `md-switch`, `md-input-container`, `md-menu` หรือ `.osc-content` โดยไม่มี Scope; ใช้ class ของหน้า/route (เช่น `.nv-hook-settings ...`) ก่อน
3. หลีกเลี่ยง `!important` ซ้อนทับหลายระดับ; ตรวจ CSS เดิมและ Ownership ก่อนเสมอ ปรับขนาด/ตำแหน่ง **เท่าที่จำเป็น** ไม่แปลง NVIDIA UI เป็นดีไซน์ใหม่
4. **ห้ามใช้ scroll เป็นวิธีแรกสำหรับปัญหาหน้าล้น**; จัดขนาดแผง ระยะห่าง และข้อมูลให้พอดี viewport ก่อน หากเนื้อหาจำเป็นต้องเลื่อน ให้อยู่ใน content region ที่ออกแบบไว้ ไม่ใช่ทั้ง OSC แบบสุ่ม
5. เว้นพื้นที่ Top bar / Settings side buttons / breadcrumbs ตาม Native; ตรวจ 100% zoom และระดับความละเอียดที่ใช้งานจริงใน CEF
6. Main-menu Tile และ Layout V5 มี geometry เฉพาะ (เช่น Group/Block/Slot): **อย่านำ responsive CSS ของ Settings ไปบังคับขนาด Tile** และอย่าทำให้ `md-menu` เดิมเสีย identity, click, focus หรือ animation
7. ตรวจ `mods/main-menu-live.css`, `mods/nv-next-v5-native.css`, `mods/preferences.css`, `mods/base.css`, `mods/osc-background.css` ก่อนเพิ่ม Override; ห้ามถือว่า legacy fixed widths เป็นมาตรฐานของหน้าใหม่
8. เช็กเข้า Settings → กลับ Main-menu, เปิด/ปิด OSC, Dropdown popup, keyboard navigation, autofocus, animation/motion และสถานะหลัง reload ทุกครั้งที่เกี่ยวข้อง

## 5. Pattern การใช้งานจริง (AngularJS Material 1.1.3)
ใช้ใน Angular template ที่คอมไพล์โดย `angular.module('main')` และมี Controller/Scope จริง:

```html
<md-input-container flex>
  <label>Transport mode</label>
  <md-select class="osc-select" ng-model="vm.mode"
             ng-change="vm.saveMode()" ng-disabled="vm.busy"
             aria-label="Transport mode" hover-focus>
    <md-option ng-value="'auto'">Auto</md-option>
    <md-option ng-value="'gpu'" ng-disabled="!vm.gpuAvailable">GPU</md-option>
    <md-option ng-value="'cpu'">CPU</md-option>
  </md-select>
</md-input-container>
<md-switch ng-model="vm.enabled" ng-change="vm.saveEnabled()"
           aria-label="Enable feature" hover-focus>Enable</md-switch>
```

- **ตัวอย่าง Route ที่ใช้งานจริง**: `mods/osc-hook-settings.js` ใช้ `$stateProvider.state(...)`, `controllerAs`, `md-select`, `md-option`, `ng-disabled`, ปุ่ม `osc-button`
- **Source ของ slider**: `mods/osc-background.js` ใช้ `md-slider`; NVIDIA original ใช้ `nv-slider` (ดู `app.js`) — เลือกตัวที่เข้ากับหน้าและทดสอบพฤติกรรมก่อน
- ใช้ `ng-model` / `ng-change` / `ng-disabled` และ `aria-label` / `hover-focus` ตาม Contract; ห้ามทำแค่ Visual state ที่ไม่ตรง API
- อย่าแทรก Angular template แบบ raw HTML ลง DOM โดยไม่ผ่าน Angular compiler/route; ตรวจ init/dispose, `$scope.$on('$destroy')`, listeners และ resource cleanup

## 6. Runtime / คุณภาพ / Deploy (ต้องรายงานตามจริง)
- ฟีเจอร์ที่ Engine ยังไม่รองรับต้อง disabled พร้อมเหตุผล ไม่เปิดปุ่ม GPU/Record/Filter เพียงเพราะ UI วาดได้
- ใช้ l10n ผ่าน `osc/l10n/` และระบบ `mods/osc-ui-localization.js`; ตรวจไทย/อังกฤษและ key ที่ขาดก่อนส่ง
- ทดสอบ Source/Unit → Build/Deploy (ถ้าผู้ใช้สั่ง) → CEF Open Cycle/Interaction → Native Host/Backend แยกกัน ระบุผ่าน/ไม่ผ่านแต่ละชั้น
- **เมื่อมีการเปลี่ยน UI ที่ต้องใช้งานจริง** ให้ Build/Deploy/Restart ตาม workflow ที่ปลอดภัยของโปรเจกต์ และตรวจว่า CEF โหลด Build ใหม่ **ไม่ใช่แค่ Source เปลี่ยน**
- งานเอกสารอย่างเดียว **ไม่จำเป็นต้อง restart runtime**; ห้ามอ้างว่า Deploy เสร็จหากไม่ได้ทำ
- ห้ามคัดลอก NVIDIA archive ทับ `Project/` ทั้งชุด; ตรวจผลกระทบ License/redistribution ก่อนแจกจ่าย assets
- บันทึกในสรุปงานว่า "**อ่าน `อ่านก่อนทำ UI.md` แล้ว**", เลือกใช้ native component อะไร, สาเหตุที่จำเป็นต้อง Custom, และผลทดสอบจริง

## 7. แหล่งข้อมูลที่ต้องเปิดตามลำดับ
1. **ไฟล์นี้**: `osc/mods/อ่านก่อนทำ UI.md`
2. `osc/index.html`, `osc/app.js`, `osc/vendor.js` (สำรวจ implementation ปัจจุบัน)
3. `osc/mods/<หน้าเป้าหมาย>.js` + `.css` และ `osc/mods/base.css`
4. `osc/README.md`, `NVIDIA NodeAPI/tools/README-NEXT-V5-NATIVE.md`, `README-NEXT-LAYOUT-V5.md` (อ่านข้อจำกัดและสถานะ historical)
5. `NVIDIA NodeAPI/tests/` โดยเฉพาะ Test ของหน้าที่เกี่ยวข้อง; ตรวจ CEF ที่ Build/runtime จริง

**ข้อยกเว้น:** หากมีเหตุผลเชิงเทคนิคที่ Native Component ใช้ไม่ได้ ให้ระบุเหตุผล ตรวจ implementation และเขียน Custom แบบ Scoped โดยไม่ทำลาย Native OSC หรือการโต้ตอบเดิม

## 8. บังคับสำหรับการสร้างหน้า Menu / Settings / Submenu / Dialog ใหม่
**ห้ามทำหน้าใหม่ก่อนจำแนกชนิดและเปิด Source ตัวอย่างของชนิดนั้น**

| ประเภทที่ต้องการ | Native OSC Pattern | Source ตัวอย่าง |
|---|---|---|
| Main-menu control / tile | ใช้ `nv-main-menu` + `md-menu-bar` / `md-menu`, ปุ่ม `.menu-button`; เพิ่ม control ผ่าน catalog/owner ที่ใช้จริง ห้าม clone Menu เพื่อเพิ่มปุ่ม | `app.js`, `mods/main-menu-live.js`, `mods/nv-next-v5-native.js` |
| หน้า Settings เต็มหน้า | `$stateProvider.state('main.xxx',...)`, `osc-content`, `osc-bread-crumb`, `nv-osc-tile`, `osc-central-div`, `preferences-root-panel`, `osc-side-buttons` | `mods/osc-renderer.js`, `mods/osc-hook-settings.js` |
| Submenu ของ Settings | State name/URL ไม่ชนเดิม, `$state.go(...)`, ชื่อ/ไอคอน/ปุ่ม Back สอดคล้อง parent ที่เปิดมา | `mods/osc-hook-settings.js`, `mods/gpu-profiles-route.js` |
| เมนู Dropdown / Context Menu | ใช้ `md-menu`, `md-menu-content`, `md-menu-item`, `md-button` ภายใต้ Angular template พร้อม handler/focus | `app.js` Main Menu |
| หน้าแบ่ง Tab | ใช้ `md-tabs` / `md-tab` ภายใน Settings shell เดิม; ไม่สร้าง route ใหม่แค่เพื่อสลับ Tab | `mods/gpu-status-panel.js` |
| Dialog / Confirm | พิจารณา NVIDIA confirmation/error ที่มีอยู่ก่อน หรือ `$mdDialog` ใน `vendor.js`; ตรวจปุ่มยืนยัน/ยกเลิก/ESC/focus จริง | `app.js`, `vendor.js` |

### 8.1 โครงสร้างหน้า Settings มาตรฐาน (Template ตัวอย่าง ไม่ใช่การเปิดหน้าใช้งานจริง)
```html
<div class="osc-content nv-next-example-settings" layout="row" ng-cloak>
  <div class="osc-bread-crumb">
    <nv-osc-tile title="Example Settings" icon-font="icon-settings"></nv-osc-tile>
  </div>
  <div class="osc-central-div" layout="column" flex>
    <div class="preferences-root-panel">
      <div class="preferences-root-label"><p>Example Settings</p></div>
      <p class="preferences-instructional-text">Example description</p>
      <md-input-container>
        <label>Mode</label>
        <md-select class="osc-select" ng-model="page.mode"
                   ng-disabled="page.busy" aria-label="Mode" hover-focus>
          <md-option ng-value="'auto'">Auto</md-option>
          <md-option ng-value="'manual'">Manual</md-option>
        </md-select>
      </md-input-container>
    </div>
  </div>
  <div class="osc-side-buttons" layout="column">
    <button type="button" class="osc-button osc-button-gray"
            ng-click="page.back()" hover-focus>Back</button>
  </div>
</div>
```

**ปรับตามหน้าจริง:** ห้าม Copy template แล้วจบ ต้องอ้างอิง DOM/Styles ของหน้า NVIDIA ที่ใกล้เคียง, l10n key, live API และขอบเขต scroll/side buttons; `nv-osc-tile` ต้องตรวจ attribute contract หากใช้ชื่อ/สถานะ dynamic

### 8.2 Route และ Controller สำหรับหน้าใหม่ (pattern ที่มีจริง)
```javascript
(function (window) {
  'use strict';
  if (!window.angular) return;

  var template = [
    '<div class="osc-content nv-next-example-settings" layout="row" ng-cloak>',
    '<div class="osc-bread-crumb"><nv-osc-tile title="Example Settings" icon-font="icon-settings"></nv-osc-tile></div>',
    '<div class="osc-central-div" layout="column" flex><div class="preferences-root-panel">',
    '<div class="preferences-root-label"><p>Example Settings</p></div>',
    '<p class="preferences-instructional-text">Page content</p>',
    '</div></div>',
    '<div class="osc-side-buttons" layout="column">',
    '<button type="button" class="osc-button osc-button-gray" ng-click="page.back()" hover-focus>Back</button>',
    '</div></div>'
  ].join('');

  window.angular.module('main')
    .config(['$stateProvider', function ($stateProvider) {
      $stateProvider.state('main.example-settings', {
        url: '/preferences/example-settings',
        template: template,
        controller: 'NvExampleSettingsController',
        controllerAs: 'page'
      });
    }])
    .controller('NvExampleSettingsController', ['$state', '$scope',
      function ($state, $scope) {
        var vm = this;
        var disposed = false;
        vm.back = function () { return $state.go('main.preferences'); };
        $scope.$on('$destroy', function () { disposed = true; });
        // เมื่อต่อ API ให้ตรวจ disposed ก่อน update และถอด listeners/timers ใน $destroy
      }
    ]);
})(window);
```

> **หมายเหตุ:** โค้ดนี้เป็นแม่แบบสำหรับสร้าง `mods/example-settings.js` เท่านั้น ต้องลงทะเบียนโหลด Script ให้ทัน Angular bootstrap ผ่าน `osc/index.html` หรือเส้นทางโหลดที่มีอยู่จริง **และทดสอบ State เปิดได้จริง**; ห้ามถือว่าไฟล์ใน `mods/` ถูกโหลดอัตโนมัติ ห้าม Copy Controller/State ชื่อเดิมจนซ้ำกัน

### 8.3 ขั้นตอนเชื่อมเมนูให้กดเข้าได้จริง (ต้องทำครบ)
1. **กำหนด Menu ownership:** หน้าใหม่เปิดจาก Main Menu / Settings / Submenu / Tile หรือ Action ใด ต้องหา controller/route/menu catalog ที่เป็นเจ้าของอยู่จริงก่อน แก้เฉพาะจุดนั้น
2. **สร้าง State + Controller + Template** ตั้งชื่อเฉพาะของตัวเอง ใช้ AngularJS DI แบบ array annotation ให้ผ่าน `ng-strict-di`; เปิดไฟล์ JS ผ่าน bootstrapping ที่ใช้งานจริง (ก่อนหรือในกระบวนการ bootstrap) และตรวจ CSS โหลดจริง
3. **ลงรายการเมนูที่จุดต้นทาง**: เรียก `$state.go('main.xxx')` ผ่าน handler ที่มีอยู่; อย่าใช้ `window.location` หรือ onclick ที่ไม่ผ่าน route; ยืนยันว่า action เปิดหน้า ไม่ใช่แค่แสดง Tile
4. **ห้ามเพิ่ม Main-menu Tile โดยแทรก DOM/ทำ clone แบบสุ่ม**: ตรวจ `mainMenu.tiles`, `mods/main-menu-live.js`, `mods/nv-next-v5-native.js`, `mods/nv-layout-v5-core.js` และฟีเจอร์ owner ปัจจุบัน; ให้ geometry/controller/click handler เดิมมีเจ้าของเดียว
5. **Back ต้องสัมพันธ์กับเส้นทางเข้า**: ถ้ามาจาก Settings ควรกลับ Settings; ถ้ามาจาก Main Menu ควรกลับ Main Menu; ถ้าเข้าได้หลายทางให้ติดตาม parent/return state อย่างปลอดภัยและมี fallback ที่ถูกต้อง อย่าฮาร์ดโค้ด Back ไป Main Menu ทุกหน้า
6. **ใช้ Native shell & controls**: breadcrumb, label, description, `preferences-root-panel`, `osc-side-buttons`, `osc-button`, `md-*`, `nv-*`, focus และ icon-font จากของเดิม ก่อนคิดหน้าตาเอง
7. **สื่อสารสถานะจริง**: busy/loading/error/empty/success, disabled เมื่อ backend ไม่พร้อม, การกลับสถานะเมื่อ save ล้มเหลว; อย่าให้กดติดแต่ API ใช้ไม่ได้
8. **ครบวงจร**: เมนูต้นทาง → หน้าใหม่ → control/save → Back → reopen → OSC close/open → locale change/reload → CEF Build ใหม่; ตรวจไม่มี duplicate route, missing script, ng-controller error, overflow, focus loss, ESC ปิด OSC ผิดจังหวะ และ stale listeners
9. **การทดสอบขั้นต่ำ**: เพิ่ม/ปรับ unit/regression ของ state/template/menu action, ยืนยัน script/css registration และ Source; หาก Deploy ให้ทดสอบบน CEF จริง ไม่อ้าง UI ผ่านเพียงเพราะ regex/unit test ผ่าน

### 8.4 กฎการออกแบบภาพ NVIDIA OSC
- **ห้ามสร้างกรอบ Settings ใหม่ทับหน้าเดิม**; ใช้ Native Settings shell และจัดพื้นที่ตามชนิดหน้า
- หน้าต่าง ๆ ควรมีลำดับชัดเจน: **Title/Breadcrumb → Description → Content/Controls → Side actions (รวม Back ตามบริบท)**
- ใช้ `md-*` สำหรับ Form/Menu/Tab ที่รองรับ แต่ปุ่มขอบขวาเป็น `osc-button` เดิมถ้าหน้านั้นใช้ pattern นี้อยู่
- ขนาดและระยะยึดแบบ NVIDIA เดิม; ห้ามเปลี่ยนรูปแบบเมนูทั้งหมดให้เป็นเว็บ Dashboard, ขยายเต็มจอ หรือเลื่อนทั้งหน้าเพียงเพื่อหนีปัญหา Overflow
- งาน Customize Main-menu ใช้ **real NVIDIA menu reduced preview / original Settings frame / current single layout owner**; อย่าสร้างภาพเลียนแบบแยกจาก handler

## 9. ต้องอ่าน UI Atlas ก่อนแกะ/สร้าง/แก้หน้าใหม่
- **คู่มือแกะโครงสร้างครบ:** [NVIDIA-OSC-UI-REVERSE-ENGINEERING.md](NVIDIA-OSC-UI-REVERSE-ENGINEERING.md) — Route 38+6, Directives/Controllers/Services, UI/Material/Icons/Fonts/Theme, Native Settings shell, CSS cascade, Mod loading 2 ช่องทาง, CEF bridge/input, l10n, Motion และ Risk Register
- **สารบัญชื่อครบสำหรับค้น Source:** [NVIDIA-OSC-UI-COMPONENT-CATALOG.md](NVIDIA-OSC-UI-COMPONENT-CATALOG.md) — 73 directives, 52 controllers, 26 services, 42 Material modules, 38 native + 6 mod states, 88 icons, JS/CSS/locale/loading inventory
- **ทุกครั้งก่อนทำหน้าใหม่หรือแก้ UI:** อ่าน Atlas + ตรวจ Source ล่าสุดเพื่อระบุเจ้าของ Menu/Route/DOM injection และลำดับการโหลด; ถ้า Runtime/CSS ถูกเปลี่ยนโดยอีก Agent ให้ยึด Source จริงก่อน Atlas
- ข้อควรระวังเป็นพิเศษ: บาง asset ถูกโหลดจาก `index.html` และยังอยู่ใน `/mods/list` ของ Host (reload ทุกประมาณ 2 วินาที), จึงต้องตรวจ double-init, Angular bootstrap order, CSS ordering และ lifecycle ก่อนเสมอ

## 10. Native NVIDIA Video Capture Presets — ห้ามตกแต่งทับ
- ปุ่ม **คุณภาพ ต่ำ / ปานกลาง / สูง / กำหนดเอง** เป็น native NVIDIA `settings-customize` / `quality-list-box` / `quality-list-item` จาก `app.js` (Angular `settingsCustomize`), ไม่ใช่ปุ่มใหม่ของ NvCapture.
- **ห้ามแก้หน้าตา/geometry/สี/border/font/hover/focus** ของ native Preset ผ่าน `video-capture-options.css` หรือ Mods อื่น ถ้าไม่ได้รับคำสั่งแก้ Preset โดยตรง; ห้ามแทนด้วยการ์ด Custom.
- หากเพิ่ม Capture Engine / API / Display / Advanced Capture ให้ Style เฉพาะ ID/Class ของส่วนที่เพิ่มเท่านั้น; หน้า Resolution/FPS อาจจัด container ตามคำสั่ง แต่ต้องรักษา native `md-select`, `nv-slider` และ Preset behavior.
- Regression: `tests/capturePipelinePlanUi.test.js` ตรวจห้ามมี `nv-preferences-video .quality-list-box` และ `nv-preferences-video .settings-customize-directive` CSS overrides; ตรวจหน้าจอ CEF จริงเมื่อเปิดอยู่ด้วย.

### 10.1 ข้อยกเว้นที่ผู้ใช้อนุมัติ: 6 NVIDIA Presets ในหน้า Video Capture (25 ต.ค. 2026)
- ลำดับ UI ภายใน **native quality-list-box / ng-repeat เดิม**: **ต่ำ | ปานกลาง | สูง | กำหนดเอง | แนะนำ | สูงสุด**; ไม่สร้างแถวใหม่ ไม่ย้าย 4 ปุ่ม NVIDIA ออก
- `mods/video-capture-options.js` เชื่อมเข้ากับ `settingsCustomize` เดิมเฉพาะหน้า Video (Manual): เพิ่ม `NvRecommended` กับ `NvMaximum` เข้า `Qualities` object, รักษา native `quality-list-item`, `selectQuality`, `isActiveQuality`, `ng-repeat`, focus/hover และ icon font
- `แนะนำ`: `icon-highlights` (nvgcshare `uniE955`) → เลือก **Medium** ที่ Native NVIDIA รองรับ; `สูงสุด`: `icon-high` (`uniE917`) → เลือก **High** ซึ่งเป็นค่าคุณภาพ Native สูงสุดที่เปิดให้ใช้ในหน้า Record (Ultra เป็นฟีเจอร์ Broadcast)
- **อย่าส่ง `NvRecommended` หรือ `NvMaximum` ไป Capture/Record API** เพราะไม่ใช่ backend quality enum; การบันทึกยังคงใช้งาน native Medium/High ตามความหมายข้างต้น
- EN/TH l10n keys: `l10n.oscCaptureRecommended`, `l10n.oscCaptureMaximum`
- **ห้ามแต่ง CSS ของ 4 ปุ่มเดิม** รวมถึงปุ่มใหม่ให้แตกสไตล์; เมื่อมีหกปุ่มควรใช้ Native layout ตามต้นฉบับก่อน ตรวจ overflow ใน CEF จริง
- Tests: `NVIDIA NodeAPI/tests/videoCaptureOptionsUi.test.js` ตรวจลำดับ 6 ปุ่ม, icon, mapping, active state และ idempotency; CEF visual acceptance ยังต้องตรวจเมื่อ Runtime เปิดใช้งาน

### 10.2 Regression Fix: native ng-repeat has 6 keys but CEF may still render only 4
- Add aliases to native `settingsCustomize.Qualities` is **not enough**: use the NVIDIA directive **isolate scope `$evalAsync()`** to request a digest. Do not rely on Capture `panelScope` injector because the native controller may initialize first.
- On the existing-instance guard, if `.quality-list-box .quality-list-item` still has fewer than six items, request another digest. Initializer must only finish when **6 compiled DOM items** are present (or capped retries expire), not just when the JS object contains six entries.
- `video-capture-options.js` now has a fallback to the compiled `quality-list-item` Angular scope to locate the original native `customizeMenu` controller without creating a second row.
- Regression: `tests/videoCaptureOptionsUi.test.js` includes `late NVIDIA isolate-scope digest paints the two extra presets...`; do not remove this case. Static tests alone cannot certify the live CEF picture.
