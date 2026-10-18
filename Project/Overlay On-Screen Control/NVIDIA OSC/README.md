# NVIDIA OSC — Native UI / Desktop Overlay Host

เอกสารนี้บอกว่าไฟล์ไหนเป็นตัวจริง ไฟล์ไหนเป็นของเก่า และแต่ละโฟลเดอร์มีหน้าที่อะไร

## หน้าที่ของโมดูลนี้

โฟลเดอร์นี้เป็น **Native C++ Host ของ OSC**: เปิด CEF, รับหน้า UI, ประกอบภาพ GPU, จัดการหน้าต่าง Desktop Overlay และประสานเฟรมไปยัง Hook Adapter

ตัวหน้า UI ที่เป็น HTML/JavaScript **ไม่ได้เก็บอยู่ในโฟลเดอร์นี้** แต่อยู่ที่:

`Project/Overlay On-Screen Control/NVIDIA NodeAPI/osc/`

Native Host โหลดหน้า UI นั้นผ่าน CEF และเชื่อมการทำงานกับ Hook Adapter ซึ่งเป็นคนละโมดูล:

`Project/NVIDIA Adapter/OSC Adapter/Duluka Hook/`

## ไฟล์ที่มีผลกับ EXE จริง

| ไฟล์ | หน้าที่ |
|---|---|
| `src/osc_main.cpp` | **Source หลักที่โปรเจกต์นี้คอมไพล์**: CEF Host, Desktop/Hook state, GPU compositor/Present, frame bridge, input, query handler, window procedure |
| `NVIDIA OSC.vcxproj` | กฎ Build ของ MSBuild/Visual Studio; ระบุ Source ที่คอมไพล์และชื่อ EXE |
| `app.rc` | Windows resource และ version metadata |
| `NVIDIA ShadowPlay.ico` | ไอคอนที่ `app.rc` อ้างถึง |
| `obj/wrapper73/libcef_dll_wrapper73.lib` | CEF wrapper library ที่ Linker อ้างถึงโดยตรง; อย่าลบเพียงเพราะเป็นไฟล์ใน `obj` |

**กฎจำง่าย:** ถ้าแก้ Host ที่รันจริง ให้เริ่มดู `src/osc_main.cpp` และ `NVIDIA OSC.vcxproj` ก่อน ไม่ใช่ไฟล์ที่มีคำว่า `full` หรือ `dumb`.

## แผนผังโฟลเดอร์

```text
NVIDIA OSC/
├── README.md                         คู่มือโฟลเดอร์นี้
├── NVIDIA OSC.vcxproj                กฎ Build ของ Host
├── app.rc                            Windows resource
├── NVIDIA ShadowPlay.ico             ไอคอน
├── src/
│   └── osc_main.cpp                  Source ที่ใช้ Build จริง
├── tools/
│   ├── cef-inspection/               สคริปต์ตรวจ CEF/query (ไม่ใช่ตัวโปรแกรม)
│   └── child-process-diagnostics/    เครื่องมือวินิจฉัย child process แยกต่างหาก
├── build_test/
│   ├── build-osc-test.ps1            สร้าง EXE ทดสอบแยกจาก output หลัก
│   ├── README.md                     ข้อควรระวังของ test build
│   ├── NVIDIA OSC_Test/              output จาก test build (สร้างภายหลัง)
│   └── obj/                          intermediate ของ test build
├── archive/
│   ├── legacy-source/                Source รุ่นเก่า/สำรอง ไม่ถูกคอมไพล์
│   ├── legacy-project/               ไฟล์ project สำรอง ไม่ใช่ project ปัจจุบัน
│   └── legacy-build-output/          ไฟล์ EXE/OBJ เก่าที่ย้ายออกจาก root
├── bin/x64/Release/                  output ปกติของ .vcxproj; build-dev ใช้เป็นต้นทาง Stage
└── obj/                              intermediate และ CEF wrapper ของ Build ปกติ
```

## จุดสำคัญใน Source หลัก

เลขบรรทัดเป็นค่าประมาณและจะเปลี่ยนเมื่อแก้โค้ด; ใช้ชื่อฟังก์ชันค้นแทนการยึดเลขบรรทัด

| เรื่อง | จุดเริ่มอ่านใน `src/osc_main.cpp` |
|---|---|
| ค้นตำแหน่ง Config, หน้า UI และ Cache | `InitBuildPaths` |
| สร้าง/ส่งเฟรมผ่าน bridge ไป Adapter | `PublishAdapterFrameLocked`, `PublishCompletedAdapterFrame`, `PublishD3D9FramePixels` |
| วาด texture และเรียก Present | `PresentToSwap`, `PresentCurrentBackBuffer`, `DrawSharedTexture` |
| ประกอบเฟรม GPU และจัดการภาพ CEF | `GpuComposite`, `OnAcceleratedPaint`, `GpuUploadComposite` |
| Worker ของ Desktop Present | `OscStartDesktopPresentWorker`, `OscQueueDesktopPresentFrame` |
| รับคำสั่งจากหน้า UI | `HandleOscQuery`, `OnQuery` |
| ป้อนเมาส์/คีย์บอร์ดและ Window messages | `ForwardKey`, `WndProc` |
| เปิด CEF และเริ่มโปรแกรม | `OnContextInitialized`, `wWinMain` |

ปัจจุบัน Source หลักยังเป็นไฟล์ C++ ขนาดใหญ่ไฟล์เดียว การแยก logic เป็นหลาย `.cpp/.h` ต้องทำเป็นงาน refactor แยกต่างหาก เพราะตัวแปร global และลำดับการเรียกกันเชื่อมโยงหลายส่วนอยู่ การย้ายในรอบนี้จัดระเบียบตำแหน่งไฟล์และทำให้ Build อ้างถึง Source ตัวจริงอย่างชัดเจน โดยยังไม่แยก logic เสี่ยง ๆ ข้าม translation unit

## Build แยกกันอย่างไร

1. **Build ของโปรเจกต์:** `NVIDIA OSC.vcxproj` ปกติลง `bin/x64/Release/` และ intermediate ลง `obj/`.
2. **Build/test แยก:** `build_test/build-osc-test.ps1` บังคับ output ไปที่ `build_test/NVIDIA OSC_Test/` และ intermediate ไปที่ `build_test/obj/`. ไม่ Stage และไม่ Deploy ไปยัง Live.
3. **Full runtime:** `Project/build.bat` เรียก `Scripts/build-dev.ps1` ซึ่ง Build หลายโมดูลและ Stage runtime ไปยัง `Build/NVIDIA ShadowPlay/`.

การ Build ไฟล์เดียวไม่ได้แปลว่าได้ Deploy runtime ทั้งชุดแล้ว และไฟล์ใน `archive/` ไม่ถูกใช้งานโดยโปรเจกต์ปัจจุบัน
