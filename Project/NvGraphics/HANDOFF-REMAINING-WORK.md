# NvGraphics — GPU Runtime Work Status (2026-10-11)

เอกสารส่งงาน สำหรับให้ AI/นักพัฒนาตัวถัดไปทำต่อ
สถานะปัจจุบัน: GPU runtime/preference/inventory chain ใช้ lane เดิม NvGraphics → NvContainer → NodeAPI → OSC
สถานะ build ล่าสุด: NvGraphics สำเร็จและวาง output ที่ `Build\NVIDIA ShadowPlay\NvGraphics`

## โครงสร้าง/เส้นทางสำคัญ

| ส่วน | พาธ |
|---|---|
| NvGraphics source | `Project\NvGraphics\NvGraphics.App` (+ `NvGraphics.Contracts`) |
| NvContainer source | `Project\NvContainer\NvContainer\NvContainer*.cs` |
| NodeAPI | `Project\Overlay OSC\NVIDIA NodeAPI\index.js`, `gpuRuntimeValidation.js` |
| OSC notifier | `Project\Overlay OSC\NVIDIA NodeAPI\osc\notifier\{notifier.js,notifier.css,adapter.js,gpu-adapter.js}` |
| Build tree (runtime จริง) | `build\NVIDIA ShadowPlay\...` (deploy = copy ไฟล์ + restart บริการของเราเอง) |
| Shared contract | `GpuRuntimePayload`, `GpuPreferencePayload`, `GpuAdapterInventoryPayload` (schema v1) — events `/Duluka/v1/GpuRuntimeState`, `/Duluka/v1/GpuPreferenceApplied`, `/Duluka/v1/GpuAdapterInventory` |

## กติกาความปลอดภัย (ต้องรักษาต่อ)

- ห้ามแตะโฟลเดอร์ `Close Project` / ห้าม git reset|clean|revert|rebase|push
- ห้ามแตะ NvContainer.exe ของ NVIDIA แท้ (`C:\Program Files\NVIDIA Corporation\`);
  service restart ทำเฉพาะ build tree ของเรา; ไม่มี game-restart action ใน OSC
- Registry Correction — `HKCU\...\DirectX\UserGpuPreferences` เป็น per-value schema
- ไม่แต้ม style เดิมของ user ใน notifier.css — เติม additive ท้ายไฟล์เท่านั้น

## งานที่ทำต่อแล้ว

1. แยก toast glyph: game-start ใช้ U+E933, GPU-change ใช้ U+E953, failure ใช้ U+E940
   และ preference ใช้ U+E94F
2. Preference toast แสดงการเปลี่ยน Windows GPU preference โดยไม่มีปุ่ม restart เกม
3. เพิ่ม GPU settings/status panel บนหน้า `preferences-mods` พร้อม switch ปิด/เปิด GPU toasts,
   รายการ runtime state, Windows preference ที่รายงานแยกจากการใช้งานจริง และ inventory
   (ชื่อ, adapter ID, PCI bus, DXGI LUID, utilization, VRAM เมื่อ telemetry มี)
4. เพิ่ม inventory event ผ่าน NvContainer/NodeAPI lane เดิม; ไม่เพิ่ม port/server ใหม่
5. ตรวจเฉพาะ executable ใน profile list เดิม; auto-check ทุก 500 ms และ manual check
   ของเกมที่เลือกใช้สอง samples ห่างกัน 250 ms; cache adapter discovery 30 วินาที
6. เพิ่ม NvGraphics เข้า `Scripts/build-dev.ps1` และ root output layout แบบแยกโฟลเดอร์
7. เพิ่ม GPU runtime, preference, inventory route schema/stale/fixed-event docs
   ใน `Project/NvConfig/OSC-DATA-CONTRACT.md`
8. เพิ่ม `Game profiles` ใน Preferences ของ NVIDIA OSC โดยใช้พื้นที่หน้าเต็มและคลาส UI เดิม;
   ปุ่มด้านขวารองรับ Tab/ลูกศร/Enter/Space และแถวเกมรองรับคีย์บอร์ด
9. ย้าย workflow ของ NvGraphics มา OSC: เพิ่ม/แก้ชื่อและ GPU ใน profile, Apply preference
    พร้อม backup/read-back verification, ลบเฉพาะ Windows entry, ลบ profile โดยไม่แตะ registry,
    restore backup, reload, refresh adapter telemetry, manual runtime check และ auto-check 500 ms
10. เพิ่ม loopback routes ที่ NvContainer สำหรับ manager CRUD/runtime/backup operations;
    reuse services จาก NvGraphics และไม่ทำ registry write ซ้ำใน NodeAPI
11. ให้ NvContainer เริ่ม/เฝ้า NvGraphics จาก `children.graphicsExe` แบบ hidden;
    Game Profiles ใน OSC จะเข้าได้เมื่อได้รับ readiness heartbeat ที่ตรวจ PID,
    executable path และ window handle แล้วเท่านั้น (heartbeat ทุก 2 วินาที, หมดอายุใน 10 วินาที)
12. เพิ่ม loopback endpoints `/v1/gpu/graphics/status`, `/ready` และ `/open`;
    ปุ่ม Open full UI ขอให้ NvContainer แสดงหน้าต่างก่อน แล้วจึงปิด OSC
13. readiness heartbeat ส่ง JSON แบบ `Content-Length` ผ่าน loopback โดยไม่ใช้ proxy;
    listener ปัจจุบันไม่รองรับ chunked transfer encoding
14. รายงาน HWND หลัง hidden form ผ่าน Shown/Hide แล้ว เพื่อไม่เก็บ WinForms handle
    ชั่วคราวจาก Load; Open ตรวจว่า window ถูกแสดงแล้ว โดยไม่ถือว่า Windows ปฏิเสธ
    SetForegroundWindow แปลว่าการแสดงหน้าต่างล้มเหลว
15. ลดระยะขอบและช่องว่างในหน้า Game Profiles เพื่อเพิ่มความสูงรายการเกม/โปรไฟล์;
    ปุ่มเรียกดู executable ปิด OSC ระหว่าง native file picker แล้วกลับหน้าเดิมพร้อมค่า
    ที่เลือก; Open full UI ปิด OSC หลังเปิดหน้าต่าง NvGraphics สำเร็จ
16. Native executable picker ใช้ foreground HWND เป็น dialog owner เพื่อให้ picker
    อยู่เหนือหน้าต่างเดิมและรับโฟกัส
17. ป้องกันการเปิด picker ซ้อนด้วย in-flight guard ทั้งใน Game Profiles UI และ
    NvContainer; OSC เพิ่ม native open-state query เพื่อรอจนปิดจริง, ยกเลิก DevTools
    close guard สำหรับการปิดที่ผู้ใช้ร้องขอ และลด z-order ชั่วคราวระหว่าง native picker
18. Open full UI ปิด OSC และรอ native close confirmation ก่อนเรียกแสดง NvGraphics
19. NvContainer และ OSC Release build ผ่าน; JavaScript syntax และ `git diff --check`
    ผ่าน; deploy แล้วและ NvGraphics readiness ตอบ `ready:true` หลัง restart
20. ทดสอบ Browse end-to-end 4 รอบต่อเนื่อง: ทุกครั้ง native ปิด OSC ก่อน dialog เปิด,
    dialog เลือก executable ได้ และกลับหน้า Game Profiles พร้อม path โดยไม่ค้าง main-menu
21. แก้ race เพิ่มเติมหลังพบอาการสลับรอบ: `openOSC()` เป็น asynchronous และคืนทันที
    ทำให้ Browse รอบถัดไปเริ่มก่อน native เปิดเสร็จ; ตอนนี้รอ native open-state แล้วจึง
    เปลี่ยนกลับ `main.gpu-profiles` และถือ in-flight guard จน route พร้อม
22. OSC อ่านรายการแอปจาก REG_SZ values ใน `HKCU\Software\Microsoft\DirectX\UserGpuPreferences`
    แล้วผสานเข้ากับ Game Profiles ตาม app identity; รองรับ path ของ desktop app และ AUMID
    ของ Store app สำหรับ Apply/System default/Remove Windows entry โดยไม่เขียน registry ตอน sync

### ข้อจำกัดที่ยังต้องตัดสินใจ/ทำต่อ

- OSC ใช้ช่องกรอก absolute executable path แทน OpenFileDialog ของ WinForms; การเพิ่มเกมที่กำลังรัน
  ยังเลือกจาก running-game list ได้ตามเดิม
- แอป Windows Graphics ถูกค้นพบจาก entries ที่มีอยู่ใน `UserGpuPreferences` ของ user ปัจจุบัน;
  แอปที่ไม่มี registry entry และรายการของ Windows user อื่นจะไม่ถูก enumerate
- Restart จะเปิด executable เดิมโดยไม่ restore command-line arguments; UI แจ้งข้อจำกัดนี้ก่อนยืนยัน

## Known limitations (อย่าแก้ด้วยการโกหก)

- Helper/child renderer: มีหลักฐาน activity ของ process อื่นจะ "รายงาน" ไม่ "attributed" — by design
- โปรเซส elevated (SYSTEM) resolve path ไม่ได้ → state Unknown
- `Inactive` ไม่ใช่ proof ว่าเกมไม่ใช้ GPU (ห้ามแสดงว่า "ไม่ใช้ GPU แน่นอน")
- Windows preference ≠ runtime usage — อย่ามีวันไหน derive อีกฝั่งจากอีกฝั่ง

## คำสั่งที่ใช้บ่อย

```
# Deploy OSC/NodeAPI: copy ไฟล์ source → build tree แล้ว restart เฉพาะโปรเซสของเรา (ดู pattern ในประวัติ session)
# Cache-bust: เปลี่ยน ?v= ใน osc/index.html ทุกครั้งที่แก้ JS/CSS
```
