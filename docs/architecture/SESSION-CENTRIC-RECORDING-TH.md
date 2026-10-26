# ShadowPlay NEXT — สถาปัตยกรรม Recording Session (ฉบับภาษาไทย)

**สถานะ:** อนุมัติเป็นทิศทางหลัก · 24 ตุลาคม 2569  
**เอกสารอ้างอิงหลัก:** [Session-Centric Recording Contract](SESSION-CENTRIC-RECORDING-CONTRACT.md) — SPN-ADR-2026-10-24-SESSION-001

> เอกสารนี้อธิบาย Architecture หลักเป็นภาษาไทยเพื่อให้นักพัฒนาและ Codex อ่านตรงกัน หากรายละเอียดขัดแย้งกันให้ยึด Contract ภาษาอังกฤษที่ลิงก์ด้านบนเป็นข้อกำหนดฉบับหลัก การมีตัวเลือกบน UI ยังไม่ได้หมายความว่า Backend นั้นทำงานได้แล้ว

## แนวคิดสำคัญ

**Duluka Capture จะมี Recording Session เป็นเจ้าของการบันทึกทั้งหมดหนึ่งชุด** ตั้งแต่เริ่มอัดจนถึงตรวจสอบไฟล์ MP4 เสร็จ ส่วน Desktop และ Fullscreen Exclusive เป็นเพียงแหล่งเฟรมที่เลือกสลับได้ ไม่ใช่ตัวเริ่ม Recording/Encoder/MP4 คนละชุด

```text
OSC เลือก Engine: Duluka / FFmpeg / OBS / NVIDIA
  |
  +-- Duluka RecordingSession [SessionId, Clock, Output, State]
  |       |
  |       +-- Display Mode Monitor
  |       |      +-- Desktop Mode   -> Desktop Capture API ที่เลือก
  |       |      +-- Exclusive Mode -> Exclusive Capture API ที่เลือก
  |       |                         (อาจใช้ DXGI ตัวเดิมได้ถ้าจับภาพได้จริง)
  |       |                 |
  |       |             Frame Router
  |       |        [LUID, Output, PTS, Format, Source Generation]
  |       |                 |
  |       |         Black/Composition Surface ภายใน
  |       |                 |
  |       +---------- Video Encoder [QSV / NVENC / AMF / Software]
  |       |                 |
  |       +-- Audio A: System [Duluka WASAPI / FFmpeg Audio*]
  |       +-- Audio B: Mic    [Duluka WASAPI / FFmpeg Audio*]
  |                         |
  |                     Session Muxer
  |                         |
  |               Finalize -> ตรวจ MP4 -> Saved/Partial/Failed
  |
  +-- FFmpeg Engine -> ใช้ Session ของ FFmpeg เอง
  +-- OBS           -> ใช้ Capture/Audio/Encoder/Mux ของ OBS
  +-- NVIDIA        -> ใช้ Capture/Audio/Encoder/Mux ของ NVIDIA
```

`*` หมายถึงช่องทางที่ต้องตรวจว่า Implement และใช้งานได้จริงก่อน

## UI ในอนาคตให้เลือกอะไรได้บ้าง?

| ตัวเลือก | แนวทาง |
| --- | --- |
| Recording Engine | Duluka / FFmpeg / OBS / NVIDIA |
| Target Display | จอที่ระบุตัวตนด้วย Monitor ID + Adapter LUID + Output Index ที่ตรวจใหม่ก่อนอัด |
| Desktop Capture API | Auto / DXGI Desktop Duplication / WGC Monitor / FFmpeg ddagrab / FFmpeg gdigrab |
| Exclusive Capture API | Auto / DXGI / WGC Monitor / Exclusive Frame Source ในอนาคตที่มีหลักฐานจริง |
| Video Encoder | Auto / Intel Quick Sync / NVIDIA NVENC / AMD AMF / Software |
| Codec / Rate Control | H.264 / HEVC / AV1 และ VBR/CQP/CBR ตามความสามารถที่ทดสอบจริง |
| Audio Capture | System กับ Mic เปิดแยกกัน และเลือก Duluka WASAPI หรือ FFmpeg Audio เมื่อพร้อม |
| Fallback | Auto แบบกำหนด API ที่อนุญาต หรือ Manual แบบล็อก API |
| Gap Policy | แสดงภาพดำสำรองแบบมีป้ายกำกับ / รักษาเฟรมเดิมแบบจำกัดเวลา / แบ่ง Segment / จบด้วยสถานะ Partial |

**เลือกได้ ≠ ใช้ได้ทุกคู่** ก่อน Start ต้องตรวจว่าจอ/GPU/Texture/API/Encoder/Pixel Format/Rate Control/PTS ไปด้วยกันได้ หากไม่ได้ต้องแสดงเหตุผลและปิดตัวเลือกนั้น ไม่ใช่แอบสลับไปอย่างอื่น

## Black Surface / Session

- ไม่ต้องเปิดหน้าต่างดำบน Windows จริงก็ได้ ให้เป็น Composition Surface ภายใน Session ที่มีความละเอียดและ Pixel Format คงที่
- Session มี Clock และ Audio Timeline เดียว; ตัว Capture Source ไม่เป็นเจ้าของเวลาและไฟล์ MP4
- ภาพดำใช้เติมช่วงไม่มีเฟรมได้ตามนโยบาย แต่ต้องนับเป็น `Placeholder` ไม่ใช่ภาพเกมจริง ห้ามปกปิดความเสียหายด้วยภาพดำไม่จำกัดเวลา
- เสียงยังอาจเดินต่อได้ระหว่างกู้ภาพ หากผู้ใช้เปิด Audio Track และอุปกรณ์ยังพร้อม

## Desktop -> Exclusive -> Desktop โดยไม่จบ Session

1. ตรวจ Display/Presentation mode และสุขภาพ Source แบบ Event + Poll จำกัด ไม่ตรวจเชิงลึกหนักๆ ทุกเฟรม
2. หาก DXGI Desktop Capture ยังได้เฟรมที่ถูกต้องเมื่อเข้า Exclusive ก็ใช้ต่อ **ไม่ต้องสลับแหล่งภาพ**
3. หากพบ `DXGI_ERROR_ACCESS_LOST` หรือ Source เสียจริง ให้สร้าง Source เดิมใหม่โดยยึด LUID/Output เดิม
4. ถ้าฟื้นไม่ได้และผู้ใช้อนุญาตให้ Fallback ให้เลือก Exclusive API ที่ *ใช้งานได้จริง* ตามรายการที่ผู้ใช้อนุญาต
5. ทุกครั้งที่เปลี่ยน Source ให้เพิ่ม Generation; ทิ้งเฟรมเก่าหรือ Callback ที่มาช้า ยืนยันอุปกรณ์/ฟอร์แมต/PTS ใหม่
6. รักษา Recording Session, เสียง และ Encoder/Mux เดิมถ้าเข้ากันได้ ถ้าความละเอียด/ฟอร์แมตเปลี่ยนจนใช้ต่อไม่ได้ ให้แปลงเข้า Canvas ที่ทดสอบแล้ว, Reconfigure, แบ่งไฟล์ Segment หรือจบ Partial/Failed
7. ห้ามเปลี่ยนไปจออื่น/Parsec โดยไม่แจ้ง ห้ามบังคับเกมออกจาก Exclusive หรือเปลี่ยน Resolution ของเกมเอง

**ไม่มี Capture API ตัวใดรับประกันจับ Fullscreen Exclusive ได้ทุกเกม** ต้องทดสอบจริงแยก DX9/DX9Ex, D3D10/11/12, Vulkan, Alt+Tab, การเปลี่ยน Resolution/Refresh และ Game Mode

## Audio และ Encoder

- System Audio และ Microphone เป็นคนละ Track มีสถานะ เปิด/ปิด, อุปกรณ์, Clock และ Provider ของตัวเอง
- `AudioEngineSession` + WASAPI มีอยู่แล้วใน Source; ส่วน `AudioSidecar.vb` ใน FFmpeg Pipeline เดิมยังเป็น **Stub ที่ไม่ได้อัดเสียงจริง**
- อย่าเปิดไมค์/เสียงทดสอบอัตโนมัติ และอย่าให้ทั้งสอง Provider อัดเสียง Track เดียวกันซ้อน
- Encoder เลือกเป็นอิสระจาก Capture API แต่ต้องตรวจ Adapter LUID และการส่ง Texture จริง; QSV อาจเก็บเฟรมไว้ก่อนปล่อย Packet
- Encoder ส่ง **Encoded Packets** เข้า Muxer; ให้ Drain/Flush ทุก Packet ก่อน Finalize
- Session/Muxer เป็นผู้สร้าง MP4 และ MediaInspector เป็นผู้ตัดสินว่า Saved จริงหรือไม่
- Intel QSV ตัวอย่าง VBR/CQP 60/60 เฟรม Decode ผ่าน ส่วน CBR ชุดทดสอบ Decode ผ่านเพียง 28/60 แม้ Encode Exit 0 — **ยังถือว่าไม่ผ่าน**

## ความหมายของ Status ที่ต้องแสดง

`Preparing -> AwaitingFirstRealFrame -> Recording -> SourceInterrupted/Recovering -> Stopping -> Draining -> Finalizing -> Verifying -> Saved / Partial / Failed / Cancelled`

ต้องแยก `Encoder Advertised`, `GPU Eligible`, `Synthetic Encode Passed`, `Real Frame Captured`, `MP4 Finalized`, `Full Decode Passed`, `Game/APIVerified`. กด Stop หรือมีชื่อไฟล์ไม่ได้แปลว่า Saved

## ของที่มีจริง กับสิ่งที่ต้องสร้าง

- มีแล้ว: Duluka `CaptureSession`, DXGI Desktop Backend, AudioEngineSession/WASAPI, Frame Sink, NVENC เฉพาะฝั่ง NVIDIA, ส่วนของการ Mux และ Media Inspection
- Intel I1: โครง Target LUID/Display Identity และ Factory อยู่ในงาน Local ของ Codex; ชุด Regression รายงาน 400 Passed / 33 Skipped ไม่ใช่หลักฐานอัดจริง
- ยังไม่ยืนยัน: Intel Native QSV เชื่อม Session, Desktop Intel -> MP4 จริง, Audio E2E บน Intel, ระบบสลับ Desktop/Exclusive อัตโนมัติ, Exclusive Source เฉพาะทาง, AMD AMF, เกม Fullscreen Exclusive แต่ละ API
- OBS/NVIDIA: ใช้ระบบบันทึกของตัวเอง ไม่ย้ายเฟรมเข้ามาใน Duluka อัตโนมัติ

## ลำดับพัฒนา

I1 GPU/Display LUID + Factory -> I2 Intel Native QSV + Decode -> I3 Desktop Real Frames to Session -> I4 เลือก Desktop/Exclusive APIs + Recovery/Fallback -> I5 Audio/Mux/Verified MP4 -> I6 OSC UI + OBS/NVIDIA Adapter Status.

ทุกเฟสต้องมี Build, Test, Regression และหลักฐานจริงก่อนประกาศผ่าน **ห้ามเปลี่ยน Architecture หลักเงียบๆ ห้าม Commit/Push หรือเขียนทับงาน Codex คนละส่วนโดยพลการ**.
