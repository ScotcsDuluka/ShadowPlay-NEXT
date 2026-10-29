# ShadowPlay NEXT — Video Capture / Advanced Capture UI v77

วันที่: 25 ตุลาคม 2026 · เครื่อง: AORUS-DESKTOP · สถานะ: Source + Build deployed, partial live CEF acceptance

> อ่าน `อ่านก่อนทำ UI.md` และ `NVIDIA-OSC-UI-REVERSE-ENGINEERING.md` ก่อนแก้ UI แล้ว คง NVIDIA native `nv-slider`, `md-select`, `md-option`, `md-switch`, `osc-button`, `preferences-root-panel`, Angular routes, API bindings และ capability guards ตามเดิม

## เป้าหมายจาก Screenshot ผู้ใช้
- หน้า Video Capture มีข้อความเล็กมาก, ช่อง Engine/API เบียดกัน, ส่วน Display/Advanced และสถานะกระจัดกระจาย, พื้นที่จัดวางไม่น่าใช้
- หน้า Advanced Capture มี dropdown สามคอลัมน์เบียดจนเกือบทับ action column, Label เล็ก, Preview/Live settings ปนกัน และลำดับชั้นไม่ชัด

## งานที่เปลี่ยน (ไม่แก้ Capture logic)
1. `osc/mods/video-capture-options.css`: เพิ่ม scoped v77 rules เฉพาะ `nv-preferences-video`: native quality choices เป็น 4 equal cards, form 2 columns, ปรับ font/spacing, รวม Display Capture เข้ากับ section, ทำ Advanced Capture action ให้ชัด และจัด footer
2. `osc/mods/advanced-capture-route.js`: ห่อ Pipeline Preview และ Live Encoder Controls ด้วย section ที่แยกเจ้าของ; ยังคง model/event/Save/Back/guard เดิมทั้งหมด
3. `osc/mods/advanced-capture-route.css`: scoped responsive 2-col grid, panel containment และ relative-width md-select (ไม่กำหนด height), read-friendly labels, NVIDIA dark grouping, status/summary
4. `osc/index.html`: เปลี่ยน asset cache query ของ Advanced CSS/JS จาก `v76` เป็น `v77`
5. `NVIDIA NodeAPI/tests/capturePipelinePlanUi.test.js`: ปรับ assertion ให้ยืนยันสอง section + 2-column responsive design

## Verification
- `node --check advanced-capture-route.js`: ผ่าน
- `node --test capturePipelinePlanUi.test.js videoCaptureOptionsUi.test.js`: 32 passed / 0 failed
- คัดลอกเฉพาะ 4 web assets ไป Build: index.html, video-capture-options.css, advanced-capture-route.css, advanced-capture-route.js
- ตรวจ SHA-256 Source/Build หลัง copy: ตรง 4/4
- ตรวจ NodeAPI HTTP `/index.html` มี v77, CSS ของทั้งสองหน้าเสิร์ฟ v77 จริง
- ตรวจ CEF 73 runtime หน้า Advanced: viewport 1920x1200, 2 sections, 2 grid columns (538px ต่อคอลัมน์), 8 md-select: `overPanel=0`, `overActions=0`, page content bottom 999px อยู่ใน central pane bottom 1120px
- **Video Capture CEF visual/geometry ยังไม่รับรอง**: ตอนสอบถาม read-only อีกครั้ง CEF เปลี่ยน route กลับ `#/base` แล้ว; ไม่ฝืนเปลี่ยนหน้า/Interrupt session ผู้ใช้; Source/tests/served assets ผ่าน แต่ต้องดู CEF screenshot ของ Video Capture อีกครั้ง
- **ไม่รับรอง FPS/Native recording** จากงาน UI นี้; ไม่มีการแก้ Capture Engine, Encoder, Hook หรือ EXE

## Deployment / Rollback
Backup ของ Build ก่อนคัดลอก:
`C:\My Project\NVIDIA-Shadowplay\build_test\OSC-UI-Redesign_Test\Backup-20261025-112131`

Rollback เมื่อจำเป็น: คัดลอกไฟล์ทั้ง 4 กลับจาก Backup ไปยังตำแหน่งเดิมใต้
`Build\NVIDIA ShadowPlay\Overlay On-Screen Control\NVIDIA NodeAPI\osc`
แล้ว reload OSC ในสถานะไม่บันทึกวิดีโอ ห้าม reset source งานของ Agent อื่น

## Follow-up QA
- เปิด `main.preferences.video` ใน CEF 1536x960 visual / 1920x1200 device pixel แล้ววัด bounds/overflow, quality tile focus, native nv-slider/4 md-select, Display md-select, Advanced button, cursor switch
- Video → Advanced → Back → Video, Save disabled state, md-select popup, ESC/Alt+Z และ locale ไทย/อังกฤษ
- หาก label ยาวต่างภาษา ให้ wrap ภายใน container ก่อนบังคับ fixed md size
