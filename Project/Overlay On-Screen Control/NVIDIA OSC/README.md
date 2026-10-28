# NVIDIA OSC — Native UI / Desktop Overlay Host

เอกสารนี้บอกว่าไฟล์ไหนเป็นตัวจริง ไฟล์ไหนเป็นของเก่า และแต่ละโฟลเดอร์มีหน้าที่อะไร

## หน้าที่ของโมดูลนี้

โฟลเดอร์นี้เป็น **Native C++ Host ของ OSC**: เปิด CEF, รับหน้า UI, ประกอบภาพ GPU, จัดการหน้าต่าง Desktop Overlay และประสานเฟรมไปยัง Hook Adapter

ตัวหน้า UI ที่เป็น HTML/JavaScript **ไม่ได้เก็บอยู่ในโฟลเดอร์นี้** แต่อยู่ที่:

`Project/Overlay On-Screen Control/NVIDIA NodeAPI/osc/`

Native Host โหลดหน้า UI นั้นผ่าน CEF และเชื่อมการทำงานกับ Hook Adapter ซึ่งเป็นคนละโมดูล:

`Project/NVIDIA Adapter/OSC Adapter/Duluka Hook/`

## Render API / Hook Transport — สถานะตาม Source 24 ต.ค. 2569

การเลือก Hook แบบ CPU/GPU ต้องยึด **Policy ใน Source** `src/OscFramePublisher/Common/OscHookTransportPolicy.h` ไม่ใช่ดูแค่ชื่อโฟลเดอร์หรือผลทดสอบ DX9Ex Mailbox:

| API | CPU | GPU | ขอบเขตจริง |
|---|---|---|---|
| D3D9 | มี | มีแบบ **Opt-in Preview** | GPU เป็น **Windowed DWM / DirectComposition** เท่านั้น; Fullscreen Classic D3D9 ยังคงใช้ CPU Bridge |
| D3D9Ex | มี | **ไม่เปิดใน Production Hook Transport Selector** | GPU Mailbox/Shared Texture มีผลทดสอบข้ามโปรเซส แต่ยังไม่ผ่านเงื่อนไข In-game Pump/Runtime |
| D3D11 | ไม่มีโหมด CPU ให้เลือกโดยตรงใน Policy | มี | GPU Shared/CPU Mirror เป็นกลไกภายใน ไม่ใช่สวิตช์ CPU ที่เปิดให้เลือก |
| D3D10 / D3D12 / Vulkan / OpenGL | ยังไม่รองรับ | ยังไม่รองรับ | มี API Probe หรือโฟลเดอร์ไม่ได้แปลว่ามี Native Renderer |

**ย้ำ:** เปิด `auto` ในค่า Setting ได้แต่ไม่เท่ากับการยืนยัน Renderer หรือ Compatibility ของ API นั้น และตารางนี้คือความสามารถตาม Source ไม่ใช่หลักฐานว่ามีการ Deploy/Hook เกมจริงสำเร็จแล้ว

## CEF 73 / DX9 FPS — หลักฐานวันที่ 25 ตุลาคม 2569

ผลทดสอบ CEF 73 แบบแยกโปรเซสอ่าน **GPU Texture Pixel จริง** พบภาพใหม่ประมาณ **60 ภาพ/วินาที** แม้ขออัตรา 75/120/240; จำนวน Accelerated Callback ที่สูงกว่าไม่ได้แปลว่าได้เฟรมภาพใหม่ตามนั้น การทดลอง Unlimited แบบไม่จำกัดอัตราได้ราว 250 ภาพ/วินาที แต่ **ไม่สามารถรักษาเพดาน FPS และการกลับสู่ 75** จึงยังไม่ใช่โหมดสำหรับ Deploy

มี Bounded CEF Source Patch อยู่ใน **Isolated Checkout** แต่ติดข้อกำหนด VS 2017 Build Tools ที่ยังไม่ผ่าน การ Build / เปลี่ยน CEF 73 ใน Runtime หลักยัง **ไม่สำเร็จและไม่ได้ Deploy** ผัง Hook ภาพขึ้นใน Floating Point x86 แบบ Windowed GPU แล้ว แต่ **ยังไม่รับรองเกม 60 FPS นิ่ง** หรือสรุปว่ารากปัญหากระตุกถูกแก้แล้ว

อ่าน [ผลวัด Fresh GPU Pixel](../../NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/docs/CEF-FRAME-DELIVERY-2026-10-25-v1.md), [ข้อจำกัด CEF Candidate Build](../../NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/docs/CEF73-BUILD-PREPARATION-2026-10-25-v1.md), [แผนวัด DX9](../../NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/docs/DX9-PERFORMANCE-VALIDATION-2026-10-25.md) และ [OSC/Backend Audit](../../../docs/osc/OSC-BACKEND-FULL-ARCHITECTURE-AUDIT-20261025.md)

## ไฟล์ที่มีผลกับ EXE จริง

| ไฟล์ | หน้าที่ |
|---|---|
| `src/osc_main.cpp` | CEF Host, Desktop/Hook state, GPU compositor/Present, input, query handler, worker/window procedure และจุดประสาน frame publisher |
| `src/OscFramePublisher/OscD3D9FramePublisher/` | D3D9 consumer backend: shared-memory BGRA pixel bridge, version/owner checks, seqlock และ frame visibility |
| `src/OscFramePublisher/OscD3D9ExFramePublisher/` | **ทดลอง DX9Ex GPU publisher**: ส่ง GPU shared texture แบบ opt-in ไปยัง D3D9Ex Hook ผ่าน mailbox/ACK; ผลทดสอบข้ามโปรเซสไม่ใช่หลักฐานว่า CEF→GPU→Hook ผ่านเกมจริงครบวงจร |
| `src/OscFramePublisher/OscD3D11FramePublisher/` | D3D11 publisher backend: shared NT texture, adapter/device selection, keyed mutex, cross-adapter staging/readback worker และ metadata bridge |
| `src/OscFramePublisher/OscD3D10FramePublisher/` | โฟลเดอร์เตรียมไว้; bridge ปัจจุบันยังไม่มี ConsumerRenderApiD3D10 |
| `src/OscFramePublisher/Common/` | ยูทิลิตีส่วนกลาง: Dirty-Rect clipping/coalescing และ readback fingerprint diagnostics |
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
│   ├── osc_main.cpp
│   └── OscFramePublisher/
│       ├── OscD3D9FramePublisher/
│       │   ├── OscD3D9FramePublisher.cpp
│       │   └── OscD3D9FramePublisher.h
│       ├── OscD3D10FramePublisher/   เตรียมโฟลเดอร์ไว้; ยังไม่มี backend
│       ├── OscD3D11FramePublisher/
│       │   ├── OscD3D11FramePublisher.cpp
│       │   └── OscD3D11FramePublisher.h
│       └── Common/
│           ├── OscFrameUtilities.cpp
│           └── OscFrameUtilities.h
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
| ประสานการส่งเฟรมไป Adapter (D3D11 shared texture/readback path) | `PublishFrameLocked`, `PublishCompletedAdapterFrame`, `QueueFrameReadbackLocked` ใน `src/OscFramePublisher/OscD3D11FramePublisher/OscD3D11FramePublisher.cpp` |
| ส่ง CPU BGRA เข้า D3D9 consumer bridge | `PublishD3D9FramePixels`, `SetD3D9FrameVisibility` ใน `src/OscFramePublisher/OscD3D9FramePublisher/OscD3D9FramePublisher.cpp` |
| Dirty rect clipping และ pixel fingerprint | `MakeAdapterDirtyBoxes`, `ComputeAdapterFrameFingerprint`, `LogAdapterFramePixelProof` ใน `src/OscFramePublisher/Common/OscFrameUtilities.cpp` |
| วาด texture และเรียก Present | `PresentToSwap`, `PresentCurrentBackBuffer`, `DrawSharedTexture` |
| ประกอบเฟรม GPU และจัดการภาพ CEF | `GpuComposite`, `OnAcceleratedPaint`, `GpuUploadComposite` |
| Worker ของ Desktop Present | `OscStartDesktopPresentWorker`, `OscQueueDesktopPresentFrame` |
| รับคำสั่งจากหน้า UI | `HandleOscQuery`, `OnQuery` |
| ป้อนเมาส์/คีย์บอร์ดและ Window messages | `ForwardKey`, `WndProc` |
| เปิด CEF และเริ่มโปรแกรม | `OnContextInitialized`, `wWinMain` |

D3D9 shared-memory pixel bridge, D3D11 shared-texture/readback backend และ Dirty-Rect/Fingerprint utilities ถูกแยกเป็น translation units แล้ว D3D11 เชื่อม Host ผ่าน `HostBindings` และ public API ที่กำหนดชัดเจน โดยคง protocol ของ shared texture/metadata และ D3D9 CPU bridge เดิมไว้ ส่วน D3D10 เป็นโฟลเดอร์เตรียมไว้ เพราะ consumer bridge ปัจจุบันยังไม่ประกาศหรือ implement D3D10

## Build แยกกันอย่างไร

1. **Build ของโปรเจกต์:** `NVIDIA OSC.vcxproj` ปกติลง `bin/x64/Release/` และ intermediate ลง `obj/`.
2. **Build/test แยก:** `build_test/build-osc-test.ps1` บังคับ output ไปที่ `build_test/NVIDIA OSC_Test/` และ intermediate ไปที่ `build_test/obj/`. ไม่ Stage และไม่ Deploy ไปยัง Live.
3. **Full runtime:** `Project/build.bat` เรียก `Scripts/build-dev.ps1` ซึ่ง Build หลายโมดูลและ Stage runtime ไปยัง `Build/NVIDIA ShadowPlay/`.

การ Build ไฟล์เดียวไม่ได้แปลว่าได้ Deploy runtime ทั้งชุดแล้ว และไฟล์ใน `archive/` ไม่ถูกใช้งานโดยโปรเจกต์ปัจจุบัน
