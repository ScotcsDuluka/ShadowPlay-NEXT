# ShadowPlay-NEXT — Project Map

คู่มือแผนที่ Source code ใต้ `Project/` เพื่อแยกให้ชัดว่าใครเป็นเจ้าของ UI, Hook, backend, capture และ runtime build

## โมดูลหลัก

| Path | หน้าที่หลัก |
|---|---|
| `Launcher.Cef/` | Native CEF launcher/bootstrap |
| `Overlay On-Screen Control/NVIDIA OSC/` | Native C++ OSC UI Host และ Desktop Overlay compositor |
| `Overlay On-Screen Control/NVIDIA NodeAPI/osc/` | ตัวหน้า UI HTML/CSS/JavaScript และ Mods ของ OSC |
| `Overlay On-Screen Control/NVIDIA NodeAPI/NVIDIA Web Helper/` | Host ของ Node/API backend |
| `NVIDIA Container/NVIDIA ShadowPlay Helper/` | Helper/Hotkey component; staged alongside the Container runtime |
| `NVIDIA Adapter/OSC Adapter/Duluka Hook/` | Hook Adapter ฝั่งเกม, native D3D hook/renderers และ host ที่โหลด Hook |
| `NVIDIA Adapter/Ansel Adapter/` | ReShade add-on สำหรับ Game Filter, render-thread commands และ named-pipe readback ไป OSC; ดู `README.md` ในโมดูล |
| `NVIDIA Capture/` | Source ของระบบ Capture และ engine ต่าง ๆ |
| `NVIDIA Container/` | Controller/runtime service และ game/runtime integration |
| `NVIDIA Graphics/` | GPU inventory, game profiles และ GPU telemetry/management |
| `NVIDIA Config/` | Config files ที่เป็น input ของ runtime |
| `FFmpeg/` | FFmpeg binaries/utilities ที่ใช้กับงาน media/capture |

## เส้นทาง Build

- `Project/build.bat` เป็น entry point ที่เรียก `Scripts/build-dev.ps1`.
- `Scripts/build-dev.ps1` คือรายการ Build ของโมดูลที่ใช้งานและขั้นตอน Stage runtime.
- Source/Intermediate ของ native OSC ปกติอยู่ใน `Overlay On-Screen Control/NVIDIA OSC/bin/` และ `obj/`.
- Test build แยกของ OSC อยู่ใน `Overlay On-Screen Control/NVIDIA OSC/build_test/NVIDIA OSC_Test/`.
- Runtime ที่ Stage แล้วอยู่ใต้ `Build/NVIDIA ShadowPlay/`; อย่าสับสนกับ Source ใน `Project/`.

## แยกอาการก่อนแก้

- **หน้า UI/Mods:** เริ่มจาก `NVIDIA NodeAPI/osc/` และ backend `NVIDIA Web Helper/`.
- **Desktop overlay / CEF rendering:** `NVIDIA OSC/src/osc_main.cpp` (CEF + Desktop compositor/Present).
- **OSC → Hook frame bridge:** API-oriented layout อยู่ใน `NVIDIA OSC/src/OscFramePublisher/`; D3D9 CPU bridge อยู่ใน `OscD3D9FramePublisher/`, D3D11 shared-texture/readback/worker/metadata อยู่ใน `OscD3D11FramePublisher/`, และ utilities อยู่ใน `Common/`. `OscD3D10FramePublisher/` เป็นโฟลเดอร์เตรียมไว้เท่านั้น เพราะ bridge ปัจจุบันประกาศ consumer API แค่ D3D9/D3D11.
- **เกม Hook / DXGI / D3D9-D3D11:** เริ่มจาก `NVIDIA Adapter/OSC Adapter/Duluka Hook/`.
- **การประกอบ runtime หรือ path ที่ deploy:** ตรวจ `Scripts/build-dev.ps1`, Config และ output ใต้ `Build/NVIDIA ShadowPlay/`.

การเปลี่ยนชื่อหรือย้าย Source ควรแก้ references ใน project/build scripts ให้ครบ แล้ว Build ตรวจสอบก่อนทดสอบ runtime ห้ามถือว่าไฟล์ที่มีชื่อคล้ายกันเป็นเจ้าของระบบเดียวกันโดยยังไม่ตรวจ project configuration
