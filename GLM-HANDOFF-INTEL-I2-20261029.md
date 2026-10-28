# GLM HANDOFF — Intel I2 Runbook (ส่งจากเครื่อง NVIDIA, 2026-10-29)

**ผู้ส่ง:** GLM (ZCode) session เครื่อง NVIDIA (AORUS-class: GTX 1080 Ti ×2, branch `Stable`)
**ผู้รับ:** GLM Agents บนเครื่อง Intel (HUAWEI-MateBook, worktree `intel/duluka-capture`, base HEAD `015f0548f82b5c1ed4d9c352fe095cc55e9a5453`)
**สถานะปลายทางที่คาดหวัง:** I1 เสร็จแล้ว (uncommitted) · I2 ยังไม่เริ่ม — ตาม [INTEL-DULUKA-CAPTURE-I2-HANDOFF](docs/INTEL-DULUKA-CAPTURE-I2-HANDOFF.md)

> อ่านก่อนแตะโค้ด: Contract หลัก `docs/architecture/SESSION-CENTRIC-RECORDING-CONTRACT.md` + ฉบับไทย + `docs/INTEL-DULUKA-CAPTURE-ARCHITECTURE.md` + I2-HANDOFF ด้านบน ทุกข้อในเอกสารชุดนี้เป็นงานเสริม ไม่ลบสัญญาเดิม

---

## 1. งานที่เสร็จบนเครื่อง NVIDIA แล้ว (2026-10-29) — สิ่งที่ต้อง "รับ"

งานทั้งหมด **uncommitted บน branch `Stable` เครื่องต้นทาง** — การขนส่ง (sync/commit/push) เป็นดุลยพินิจของเจ้าของ repo ทั้งสองฝั่ง Agents ห้าม commit แทน

| # | ไฟล์ | การเปลี่ยนแปลง | ความหมายกับเครื่อง Intel |
| --- | --- | --- | --- |
| 1 | `Project/NVIDIA Capture/EngineCapture/EngineCapture.slnx` | เดิมมี 29 โปรเจกต์ → **36** (เพิ่ม: QsvNative.Tests, Recovery.Tests, SessionRecovery.Tests, MediaEvidence.Tests, Mux.Tests, RouteGuard.Tests, Routing.Tests, Routing) — **เอา LegacyCompletion.Tests ออก 1 ตัว** เพราะ build พังทันทีหากไม่มี `-p:CaptureAssemblyPath` (วิธี build แยกอยู่ใน README ของ EngineCapture) | รวมชุด Intel harness เข้า solution เดียว |
| 2 | `Project/NVIDIA Capture/EngineCapture/Invoke-EngineOfflineChecks.ps1` | ไฟล์ใหม่: build slnx + รัน 26 ชุด device-free ในคำสั่งเดียว | จุดตรวจแรกหลัง sync |
| 3 | `Project/NVIDIA Capture/EngineCapture/README.md` | ไฟล์ใหม่: แผนที่ระบบ ADR→ไฟล์จริง + สถานะจริงทุกส่วน | อ่านก่อนทำงาน |
| 4 | `Project/NVIDIA Capture/EngineCapture/QsvNative.Tests/Program.cs` | เพิ่ม environment gate (ดู diff ข้างล่าง) | บนเครื่อง Intel ที่มี output จริง suite จะ**รันเต็ม** ไม่ skip |

**Patch ของไฟล์ #4 (ใช้ต้นทาง: `Program.Main` บรรทัดที่เดิมเป็น `Single(...)` ตรง ๆ):**

```csharp
// Environment gate: this suite probes the REAL native QSV runtime and
// therefore requires a physical eligible Intel output. On machines
// without one it must SKIP truthfully (like Video.Tests HardwareGate),
// never crash with an enumeration exception.
var intelOutputs=DxgiDisplayInventory.Collect().Outputs.Where(t=>t.Eligible && t.Adapter.Vendor==GpuVendor.Intel).ToList();
if(intelOutputs.Count==0) {
    Console.WriteLine("QSV_NATIVE_SKIP reason=no_eligible_intel_output screen_pixels_read=false audio_opened=false");
    return 0;
}
var target=intelOutputs.Single(t=>t.Eligible && t.Adapter.Vendor==GpuVendor.Intel);
```

หาก worktree ปลายทางมี `Program.cs` ต่างจากนี้ (งาน Codex I1/I2 local) — **ใช้เวอร์ชันปลายทางเป็นหลัก** แล้ว apply เฉพาะแนวคิด gate (count==0 → SKIP) อย่างระมัดระวัง ห้าม revert งาน local

**ผลที่ยืนยันแล้วบนเครื่องต้นทาง:** `ENGINE_OFFLINE_CHECKS_PASS suites=26` (build 0 error) · QSV สัญญา device-free 3 โหมด = 94 checks ผ่าน · NVENC E2E synthetic→MP4→full-decode ผ่านบนการ์ดจริง

---

## 2. Runbook บนเครื่อง Intel (รันตามลำดับ — หยุดทันทีเมื่อ FAIL)

```powershell
cd 'C:\My Project\NVIDIA-Shadowplay'   # worktree intel/duluka-capture

# ขั้น 0 — สภาพแวดล้อม: ยืนยัน branch/HEAD + งาน uncommitted ยังอยู่ครบ
git status --short          # ต้องเห็นงาน I1 local ตาม I2-HANDOFF — ห้าม reset/checkout ทับ
git rev-parse HEAD          # คาดหวัง 015f0548... (ยกเว้นเจ้าของ repo เลื่อนเองแล้ว)

# ขั้น 1 — รับงาน 4 ไฟล์จากต้นทาง (transport โดยเจ้าของ repo) แล้ว build ทั้งชุด
dotnet build "Project\NVIDIA Capture\EngineCapture\EngineCapture.slnx" -c Release
# เกณฑ์: Build succeeded, 0 error

# ขั้น 2 — ชุด device-free รวม (ไม่จับหน้าจอ ไม่เปิดไมค์)
powershell -ExecutionPolicy Bypass -File "Project\NVIDIA Capture\EngineCapture\Invoke-EngineOfflineChecks.ps1"
# เกณฑ์: ENGINE_OFFLINE_CHECKS_PASS suites=26
# ข้อแตกต่างที่ยอมรับได้บนเครื่อง Intel:
#   - EncoderSynthetic: NVENC cases → SKIP (ไม่มี NVIDIA) — ต้องเป็น SKIP ไม่ใช่ FAIL
#   - QsvNativeHardwareGate: ต้อง "รันจริง" ไม่ใช่ SKIP (เพราะมี Intel output จริง)
#   - ชุดอื่นต้องได้ตัวเลขใกล้เคียงต้นทางทั้งหมด

# ขั้น 3 — I1 baseline เดิมต้องยังเขียว (งาน uncommitted ไม่โดนกระทบจากการรับไฟล์)
& '.\Project\NVIDIA Capture\EngineCapture\Invoke-IntelCaptureI1Regression.ps1'
# เกณฑ์อ้างอิงจากรอบก่อน: 400 passed / 0 failed / 33 skipped (ตัวเลขอาจเล็กน้อยตาม topology จริง)
# หมายเหตุ worker: ถ้าไม่มีโปรเซส NvCapture อยู่เลย → ข้าม health guard แล้วรันต่อได้
#   แต่ถ้ามี worker รันอยู่แล้ว มันต้อง ready+idle ไม่งั้นสคริปต์ throw — ห้ามสตาร์ท worker เพื่อผ่าน guard

# ขั้น 4 — I2 safe regression (QSV hardware จริง + mux fault + media ที่ retain ไว้)
& '.\Project\NVIDIA Capture\EngineCapture\Invoke-IntelCaptureI2Regression.ps1'
# เกณฑ์: I2_SAFE_REGRESSION_PASS, QSV_NATIVE_PASS ... screen_pixels_read=false
# บรรทัดสำคัญ: ACTUAL_QSV_IDENTITY ต้องชี้ physical Intel LUID จริง (ไม่ใช่ Parsec, ไม่ใช่ software)

# ขั้น 5 — I3 transport (synthetic owned resources เท่านั้น)
& '.\Project\NVIDIA Capture\EngineCapture\Invoke-IntelCaptureI3Regression.ps1'
# เกณฑ์: I3_SAFE_TRANSPORT_PASS + I3_SAFE_REGRESSION_PASS
```

**ทดสอบ QSV แบบเจาะจง (เรียกตรงได้เมื่อต้องไล่ปัญหา):**

```powershell
dotnet run --project "Project\NVIDIA Capture\EngineCapture\QsvNative.Tests\QsvNative.Tests.csproj" -c Release --            # เต็ม: Probe vbr+cqp, SessionAcceptance vbr+cqp, FailureAndRecovery
dotnet run --project ... -- --i3b            # owned NV12 → QSV probe (พิสูจน์ device identity)
dotnet run --project ... -- --i3c            # D3D11 transport
dotnet run --project ... -- --i3-transport   # pixel/D3D11/frame-adapter transport
dotnet run --project ... -- --session-contract-only | --native-source-fault-synthetic-only | --session-media-synthetic-only
dotnet run --project ... -- --verify-existing-media <mp4> <ffmpeg> <ffprobe> <v?h?> <w?> <fps?> <?>   # 7 args ตาม Program.cs
```

---

## 3. เกณฑ์ยอมรับ I2 (ย่อจาก I2-HANDOFF ข้อ 1–9 — อ่านต้นฉบับก่อนลงมือ)

1. Toolchain native (VC/cmake ยังไม่มีใน PATH — I1 ไม่ได้ติดตั้งอะไร) + versioned oneVPL/header packaging; dispatcher เจรจาที่ API 1.34 — ห้ามสมมติว่ามี surface 2.x ต้อง probe ก่อน
2. QSV bridge แยกโปรเจกต์ implement `IEncoderRuntimeBackend` — bind Intel D3D11 ที่สร้างเอง + รายงาน parent adapter identity จริง; fail ถ้า software implementation / LUID ผิด / capability ขาด
3. พิสูจน์ `synthetic owned NV12 → H.264 → MP4 → นับ packet/frame → full decode` **ก่อนเปิด provider** — เริ่ม VBR/CQP
4. **CBR ยังเป็น known-failure** (รอบเดิม: 60 packet อ่านได้ แต่ decode ผ่าน 28/60) — `SessionTests` มี assertion `cbr_known_failure` ให้ CBR ถูก reject **ห้ามแปลงเงียบๆ เป็น VBR**; ปลดได้เมื่อแก้รากเหตุ + ผ่าน full-decode
5. `Providers intel:qsv-h264 Implemented=False` คงไว้จนกว่าหลักฐานเพียงพอ; **ห้าม bypass** `RequireWired` / physical eligibility / current-target / `RequireIdentity`
6. Mapping rate-control ต้อง provider-aware (`cqp` ของ Intel ส่งผ่าน `ResolveRateControl` เดิมไม่ถูกต้อง — เดิมเป็น semantics NVENC)
7. **ช่องว่างที่บังคับต้องทำ:** Drain `Flush(packetSink)` ทุก packet เข้า mux ก่อน encoder stop/mux completion — `CaptureSession` ปัจจุบันยังไม่ drain delayed encoder — **ห้ามเปิด QSV จนมี test ครอบคลุมข้อนี้**
8. เพิ่มหลักฐาน media verification ให้ `SessionResult` — `VideoIdentity` เป็น device metadata เท่านั้น ไม่ใช่หลักฐานเฟรม
9. จบรอบ: build Release → รัน I1+I2+I3 regression → รายงาน pass/fail/skip จริง **แล้วหยุด** รอ phase ถัดไป

---

## 4. ข้อห้าม (ตรงตามสัญญา ADR)

- **ห้าม**จับหน้าจอ/เสียงจริง — ไม่มี live DXGI capture ที่ไหนใน runbook นี้; I3 live ต้องรอฉาก privacy-cleared + ขอบเขตอนุมัติจากผู้ใช้ชัดเจน
- **ห้าม** capture จอ Parsec/จอเสมือน (MateBook มี `ROOT\DISPLAY\0000` = Parsec — I1 เรียง identity ไว้ให้แล้ว อย่าเขียนทับ logic นั้น)
- **ห้าม**รัน Video suite แบบไม่ใส่ `--offline` (default มันเปิด desktop duplication จริง)
- **ห้าม** commit/push/reset งาน uncommitted I1 ของกันเองหรือของเครื่องอื่น — transport ทั้งหมดโดยเจ้าของ repo
- **ห้าม**ประกาศว่า "อัดได้จริง" จาก build ผ่าน / exit 0 / ไฟล์มีหัว MP4 — ต้องได้ full-decode ครบตามจำนวนเฟรมที่ deterministic
- การ restart worker/deploy: นอกขอบเขตเอกสารนี้ — ให้แยกขออนุมัติเป็นรายงาน

## 5. สถานะที่เกี่ยวแต่ไม่ใช่งานฝั่ง Intel

- **UI osc/data osc ยังไม่เชื่อมโดยเจตนา** — NodeAPI shims 4 ตัวมีโค้ด+test แต่ยังไม่ถูก mount ใน `Backend/index.js` (รอ Backend/Module นิ่งตามคำสั่งผู้ใช้) — แผนอยู่ `EngineCapture/README.md` §6 อย่าแตะในรอบ I2 นี้
- Container `policyValid=false` (`FileNotFoundException` ของ capture routing policy) เป็นงานฝั่ง Container/Capture policy แยกต่างหาก
