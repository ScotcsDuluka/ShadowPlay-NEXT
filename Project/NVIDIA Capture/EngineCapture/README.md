# EngineCapture — Duluka Capture Engine (แผนที่ระบบฉบับเดียว)

อัปเดต: 2026-10-29 · เครื่องพัฒนานี้ (NVIDIA: GTX 1080 Ti ×2) · งาน Intel/QSV อยู่อีกเครื่อง

สัญญาสถาปัตยกรรมหลัก: [SESSION-CENTRIC-RECORDING-CONTRACT](../../../docs/architecture/SESSION-CENTRIC-RECORDING-CONTRACT.md) (SPN-ADR-2026-10-24-SESSION-001) · [ฉบับภาษาไทย](../../../docs/architecture/SESSION-CENTRIC-RECORDING-TH.md)
เอกสารนี้คือ "แผนที่" ไม่ใช่ข้อกำหนด — หากขัดแย้งกับ Contract ให้ยึด Contract

---

## 1. สร้างและตรวจทั้งระบบด้วยคำสั่งเดียว

```powershell
# build ทั้ง 36 โปรเจกต์ + รันชุดทดสอบ device-free ทั้งหมด (ไม่จับหน้าจอ/ไม่เปิดไมค์)
powershell -ExecutionPolicy Bypass -File .\Invoke-EngineOfflineChecks.ps1

# build เฉยๆ
dotnet build EngineCapture.slnx -c Release
```

ผลล่าสุด (2026-10-29): **ENGINE_OFFLINE_CHECKS_PASS suites=26, build 0 error** — สรุปอยู่หัวข้อ 4

`LegacyCompletion.Tests` ถูกเว้นจาก solution เพราะต้อง DLL stage ที่ validate แล้ว:
`dotnet build LegacyCompletion.Tests -p:CaptureAssemblyPath=<staged>/NvCapture.dll`

---

## 2. ชั้นระบบ: ADR component → ไฟล์จริง

```
RecordingEngine (VB) ───────────── Duluka Capture/Recording/DulukaEngine.Recording/RecordingEngine.vb
 └─ RecordingBackendFactory        คัด provider ตาม vendor/LUID — Intel ถูก fail-closed (Implemented=False)
 ├─ CaptureSession                 Recording/DulukaEngine.Recording/CaptureSession.vb (SessionId/Clock/State)
 ├─ FrameRouter                    Recording/DulukaEngine.Recording/SessionFrameRouter.vb
 ├─ DesktopFrameSource (DXGI)      Video/DXGI Desktop Duplication/DulukaEngine.Video.DXGI/DdagrabBackend*.vb
 ├─ VideoEncoder NVENC (VB)        Encoding/NVENC/DulukaEngine.Encoder.Nvenc/NvencEncoderBackend.vb
 ├─ VideoEncoder QSV (C#)          Encoding/Intel/DulukaEngine.Encoder.Qsv/QsvEncoderBackend.cs (path NV12 เท่านั้น)
 ├─ Audio WASAPI (C#)              Audio/DulukaEngine.Audio.Wasapi/ + Audio/DulukaEngine.Audio/AudioEngineSession.cs
 ├─ FFmpeg backend                 Recording/Backends/FFmpeg/DulukaEngine.FFmpegBackend/ (engine แยกของตัวเอง)
 ├─ MediaInspector/MediaVerifier   Recording/Media/DulukaEngine.Media/SessionMediaVerifier.vb
 └─ ตัวขับจริง (CLI)               Tools/DulukaEngine.Recording.ConsoleDriver/ (โหมด --videocheck ใช้ config chain จริง)

โฮสต์ production:  ../NVIDIA Capture.vbproj (NvCapture.exe, REST :59077)  ← ตัวจบวงจร UI
Control plane:     OSC UI → NodeAPI :59011 → NvContainer :59012 → NvCapture :59077
```

## 3. สถานะจริงของแต่ละส่วน (ไม่ตกแต่ง)

| ส่วน | สถานะ | หลักฐาน |
| --- | --- | --- |
| DXGI Desktop Duplication (Ddagrab) | **Wired, ใช้ production** | `RecordingBackendFactory.CreateCapture` |
| Native NVENC H.264 | **Wired, ใช้ production** | synthetic 69/69 บน 1080 Ti จริง |
| Session กลาง (CaptureSession/Clock/Router) | **Wired** | Recording.Tests 41 pass, owned-frames path |
| FFmpeg engine (แยก) | **Wired** | LiveMux synthetic 13 + audio-intent 5 |
| MediaVerifier (ตัดสิน Saved) | **Wired** | Mux 25 + MediaEvidence 25 + decode integrity |
| QSV encoder (C#) | **มีโค้ด, fail-closed บน path BGRA/live** | `Providers intel:qsv-h264 Implemented=False` → `intel_native_live_input_not_verified`; path NV12 ใช้ได้ผ่าน `CreateEncoderForInput` |
| WgcCapture | **ไม่ถูก wire** — ไม่มีใคร reference | grep ทั้ง workspace |
| AudioSidecar.vb (FFmpeg) | **Stub ตามสัญญา** (no-op lifecycle) | หัวไฟล์ประกาศชัด |
| ExclusiveFrameSource / ModeMonitor สลับ Desktop↔Exclusive อัตโนมัติ | **ยังไม่ implement** (I4) | ADR §I |
| OBS/NVIDIA-engine ภายนอก | คง pipeline ของตัวเอง | ADR §B-12 |

## 4. ชุดทดสอบ (รันผ่านล่าสุดทั้งหมด)

| ชุด | ผล | พิสูจน์อะไร |
| --- | --- | --- |
| AudioOffline | 50 + 1,167 | timeline เสียง/สถานการณ์ขอบ/ไม่แตะอุปกรณ์จริง |
| CoreTests / ConfigTests / FrameContract | 14 / 95 / 8 | แกน config v2 + contract เฟรม |
| RecordingUnitTests | 41 pass, 5 skip | session lifecycle/stop hardening (มี stress 50×) |
| EncoderSynthetic | 69/69 | **NVENC จริงบน 1080 Ti** + config/GOP/preset mapping |
| B1 / I3DFrame / I3DWorker | 5 / 27 / 10 | quiescence + ownership เฟรม (fake frames ล้วน) |
| FFmpeg synthetic ×2 | 13 + 5 | mux lifecycle/audio intent |
| Routing / RouteGuard | 14 / 33 | ตาราง route + sandbox guards |
| Composition / DisplaySelection | 82 / 21 | factory/LUID/จอเสมือน (synthetic targets) |
| HardwareReadiness `--dxgi-only` | 8 | อ่าน metadata DXGI อย่างเดียว (ไม่แตะพิกเซล) |
| QsvNative contract/fault/media | 35 + 16 + 43 | สัญญา session (device-free) |
| QsvNativeHardwareGate | **SKIP** `no_eligible_intel_output` | เครื่องนี้ไม่มี Intel — ต้อง skip จริงใส่ crash |
| RecordingRecovery / SessionRecovery | 15 / 29 | กู้คืน + lifecycle ไฟล์จริง (lavfi) |
| MediaEvidence / Mux / DeepDecode / LongScan | 25 / 25 / 5 / 2 | หลักฐานไฟล์ + full decode + scan 1/5 นาที |

นอกชุดอัตโนมัติ — **พิสูจน์แล้ววันที่ 2026-10-29**: `lavfi → h264_nvenc (1080 Ti) → MP4 → ffprobe 15/15 frames → SessionMediaInspector full-decode PASS`
(เทียบเท่า Phase 7D; สคริปต์ `Invoke-IsolatedNvencSyntheticSmoke.ps1` ตัวเต็มต้องมี worker :59077 idle จึงข้าม)

**ไม่รันอัตโนมัติ (ต้องอนุมัติ/เงื่อนไข):**
- `DulukaEngine.Video.Tests` ส่วน DDAGRAB (เปิด Desktop Duplication จริง — ต้องฉากทดสอบไร้ความเป็นส่วนตัว)
- `ConsoleDriver` (อัดจริง) และ `IsolatedTest/Invoke-CaptureSmoke.ps1` (ต้อง confirm privacy ×2 + worker + pairing secret)
- `Invoke-CaptureOfflineGate.ps1` ตัวเต็ม (ต้อง worker :59077 พร้อม/idle)

## 5. สคริปต์

| สคริปต์ | บทบาท | เงื่อนไข |
| --- | --- | --- |
| `Invoke-EngineOfflineChecks.ps1` | **จุดเดียว**: build + รัน 26 ชุด | FFmpeg bundled + NvCapture.dll |
| `Invoke-CaptureOfflineGate.ps1` | release gate 8 ขั้นตอน | worker :59077 idle (fail-closed) |
| `Invoke-CaptureHardwarePreflight.ps1` / `...RelayReadOnlyPreflight.ps1` | เช็คฮาร์ดฯ/relay อ่านอย่างเดียว | ตามสคริปต์ |
| `Invoke-IsolatedNvencSyntheticSmoke.ps1` | NVENC จริง + inspector | worker idle |
| `Invoke-IntelCaptureI1/I2/I3Regression.ps1` | regression ฝั่ง Intel | **เครื่อง Intel เท่านั้น** |
| `IsolatedTest/Invoke-CaptureSmoke.ps1` | smoke E2E ผ่าน REST | privacy confirm ×2 + worker |

## 6. การเชื่อม UI osc / data osc — สถานะปัจจุบัน (รอ Backend/Module แข็ง)

โซ่ที่ถูกต้องตาม [Container contract](../../NVIDIA%20Container/CAPTURE-COMMUNICATION-HUB-CONTRACT-20261024.md):
`OSC UI → NodeAPI :59011 → NvContainer :59012 → NvCapture :59077`

**ช่องว่างที่วินิจฉัยแล้ว (2026-10-29):** หน้า UI เรียก `/DulukaCapture/v.1.0/{status,engines,displays,display/select,screenshot}`
ที่ NodeAPI แต่ `NVIDIA Web Helper/Backend/index.js` **ไม่ได้ mount shims ทั้ง 4 ตัว** —
`shims/capture-runtime-status.js`, `capture-engine-status.js`, `capture-display-routing.js`, `capture-capabilities.js`
มีโค้ด + test ครบแล้ว (tests/capture*.test.js) แต่ไม่มีใคร require ใน production → UI ได้ 404 ทั้งหมด

**แผนเชื่อมเมื่อได้สัญญาณ (ข้ามได้ทันที):**
1. เพิ่ม 4 บรรทัด `require('./shims/...')(app, ctx)` ใน index.js หลัง middleware/ก่อน route-floor
2. รัน `node --test tests/` (shim tests เขียวอยู่แล้วเมื่อรันตรง)
3. ตรวจ live: `GET :59011/DulukaCapture/v.1.0/status` ต้องได้ 200 + worker truth จริง (ไม่ mask recording)

**ยังไม่แก้ตามคำสั่งผู้ใช้** — Backend/Module ยังไม่นิ่ง; ห้าม deploy/restart จนกว่าจะบอก

ประเด็นที่เกี่ยวแต่ไม่ใช่ NodeAPI: Container รายงาน `policyValid=false` (`FileNotFoundException` ของ capture routing policy) — เป็นของฝั่ง Container/Capture policy ตาม [NODEAPI-CAPTURE-STATUS-GET-FIX-20261024](../Overlay%20On-Screen%20Control/NVIDIA%20NodeAPI/NODEAPI-CAPTURE-STATUS-GET-FIX-20261024.md)

## 7. เอกสาร phase (เรียงตามเวลา)

- แกนเอนจิน: PHASE1–4 (build→NVENC→audio→หยุดปลอดภัย) · PHASE5A–D (offline ชุดแรก)
- เสถียรภาพ/หลักฐาน: PHASE6A–H (stability→outcome→recovery→synthetic→MP4→scan→decode→**release gate**)
- ฮาร์ดแวร์: PHASE7A–D (preflight→LUID gate→DXGI topology→NVENC synthetic จริง) · PHASE8 (display/screenshot)
- อื่นๆ: [ENGINE-ROUTER.md](ENGINE-ROUTER.md) (ตาราง route engine), [Duluka Capture README](Duluka%20Capture/README.md)

## 8. กติกาที่ห้ามละ

- ห้ามเขียนทับ/commit ทับงาน concurrent (Codex) — ทุกอย่างที่เพิ่มที่นี่เป็น additive
- ห้ามจับหน้าจอ/ไมค์จริงโดยไม่มีฉากทดสอบ + ขอบเขตชัดเจน; synthetic/read-only ได้ในขอบเขตงาน
- ฟีเจอร์ที่ยังไม่ผ่าน live gate ต้องรายงานว่า Unverified — ห้ามเรียก "เสร็จ" จาก build หรือ UI เพียงอย่างเดียว
- งาน Intel (QSV native live, E2E audio Intel) อยู่เครื่อง Intel — ชุด `Invoke-IntelCaptureI*.ps1` + QsvNative โหมด hardware
