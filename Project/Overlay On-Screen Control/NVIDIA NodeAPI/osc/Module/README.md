# ShadowPlay NEXT — Shadow Module Architecture v1

**Scope:** Source-only diagnostic migration. Original NVIDIA source index.html/app.js/vendor.js/common.js, legacy mods/ and the existing Production Build remain unchanged by this migration.

## Folders
- Module/Base/ — shared UI, keyboard/hotkeys, language services.
- Module/MainMenu/ — NVIDIA original layout integration, Freestyle V5, Group Editor and Legacy V4 model.
- Module/Overlay/ — Motion, Background, FPS, Renderer. Motion is optional and must not own actual native window visibility.
- Module/Settings/ — Capture, Hook, GPU, notifications, ReShade. Each has its own route ownership.
- Module/Gallery/, GameFilter/, Notifier/ — feature owners.
- Module/Core/ — manifest-driven profile generator and an optional diagnostic gate. Core/ itself must never depend on optional modules.
- Module/docs/ — mandatory UI guides, audit reports and migration records (moved from the retired mods/).
- Module/module-manifest.json — SHA-256 and owner mapping for every mirrored asset.

## แผนที่โฟลเดอร์ (สำหรับเปิดหาไฟล์)

| โฟลเดอร์ | ข้างในคืออะไร | โหลดตอน runtime? |
|---|---|---|
| `Base/` | UI พื้นฐานร่วม, hotkey, ระบบภาษา/ฟอนต์ไทย | ✓ (static) |
| `Core/` | เครื่องมือ generator/ตรวจสอบ + late-extensions loader | เฉพาะ loader ตัวเดียว |
| `Gallery/`, `GameFilter/`, `Notifier/` | Gallery, Game Filter/Photo Mode, ระบบ toast | ✓ |
| `MainMenu/` | ระบบเมนูหลัก — ดูตาราง 4 รุ่นด้านล่าง | บางส่วน |
| `Overlay/` | Motion (แอนิเมชัน), Background, FPS, Renderer | ✓ |
| `Settings/` | หน้าตั้งค่าทั้งหมด: Capture, Hook, GPU, Notifications, ReShade | ผ่าน late loader |
| `docs/` | คู่มือบังคับ + รายงาน + บันทึก migration | — |

## MainMenu — 2 รุ่นที่ยังใช้ (ตัดรุ่นเก่าแล้ว 2026-10-29)

| โฟลเดอร์ | บทบาท |
|---|---|
| `NVIDIA/` | V2 live customization บนเมนู native เดิม (main-menu-live) |
| `OSCFreestyleV5/` | **รุ่นปัจจุบัน** — V5 layout + Freestyle editor + unified model (data layer ที่ V5 ใช้) |

รุ่นเก่า (LegacyV4 native UI, GroupEditor) ถูกลบเมื่อ 2026-10-29 เพราะ production ไม่เคยโหลด; ประวัติอยู่ใน docs/CUTOVER-20261029.md และ git history
