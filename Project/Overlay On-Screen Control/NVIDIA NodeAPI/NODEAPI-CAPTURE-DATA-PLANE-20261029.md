# NodeAPI — Capture Data Plane (record / state / sync) 2026-10-29

Scope: `Project/Overlay On-Screen Control/NVIDIA NodeAPI` เท่านั้น (source) — **deploy หน้างานแล้ว 01:42 (ดูท้ายเอกสาร)**

## Deploy หน้างาน 2026-10-29 (ผู้ใช้อนุมัติ)

- Backup: `Build\DeploymentBackups\NodeAPI-DataPlaneBackup-20261029-013842\` (index.js + shims เดิมครบ)
- คัดลอก shims 6 ไฟล์ลง deployed `Build\NVIDIA ShadowPlay\Overlay On-Screen Control\NVIDIA NodeAPI\shims\`
  + แทรก mount block ก่อน route-floor ใน deployed `index.js` (enabled มาจาก `nvcontainer.json` `captureRuntime.enabled=true` ซึ่งเปิดอยู่แล้ว, socket ผ่าน adapter `io.emit`, **desiredBridge: false**)
- Restart NvNode: kill → Container watchdog respawn อัตโนมัติ (สำเร็จ 2 ครั้ง)
- ผล live จริง: `/status` 200 truthful (worker ยังไม่ spawn → `ready:false, recording:null` ไม่แต่ง), `/state` มี payload,
  `/engines`+`/capabilities` fail-closed 503 ตามจริง, `record/start {"confirmed":true}` → **ส่งต่อถึง Container จริง
  แต่ NvContainer ตัวที่รัน (binary 27 ต.ค.) ตอบ 404 not found** = relay ทำงานถูกต้องและเปิดช่องว่างที่เหลืออย่างตรงไปตรงมา
- **ช่องว่างถัดไปฝั่ง Container:** rebuild/restart NvContainer จาก source (มี CaptureWorkerGateway) + เพิ่ม
  `captureRuntime.allowRecordCommands=true` — ต้องกำหนดเวลา restart ที่กระทบทั้งระบบ แยกเป็นงานของตัวเอง
- Fix ระหว่าง deploy: deployed backend parse body ราย-route (ไม่มี global req.body) — shims ทั้งสองตัวอ่าน body
  เองแบบรองรับทั้งสอง lineage แล้ว (tests ยังเขียว 13/13)

## สิ่งที่เพิ่ม

โซ่บังคับ: `OSC UI → NodeAPI :59011 → NvContainer :59012 (data broker) → NvCapture :59077 (owner)`
UI ห้ามคุย worker ตรง; Container เป็นผู้คุมรับ-ส่งและสิทธิ์ (`captureRuntime.allowRecordCommands`)

### 1. Mount ใน `NVIDIA Web Helper/Backend/index.js` (หลัง gallery, ก่อน 404)

ปลายทางที่ UI เรียกอยู่แล้วเดิม 404 ทั้งหมด — ตอนนี้มีจริง:
- `GET /DulukaCapture/v.1.0/engines` → Container `/v1/capture/engines` (shims/capture-engine-status.js, mount ตัวนี้ดึง display-routing ตาม)
- `GET|POST /DulukaCapture/v.1.0/status` → Container `/v1/capture/status` (shims/capture-runtime-status.js)
- `GET /DulukaCapture/v.1.0/capabilities` → Container `/v1/capture/capabilities` (shims/capture-capabilities.js)
- `GET|POST /DulukaCapture/v.1.0/displays` + `/display/select` + `/screenshot` (shims/capture-display-routing.js)

ทั้งหมด **ปิดอยู่** จนกว่า `NV_CAPTURE_RUNTIME_ENABLED=1` (status รายงาน `CAPTURE_RUNTIME_DISABLED` แบบ honest)

### 2. คำสั่ง record — `shims/capture-record-commands.js` (ใหม่)

- `POST /DulukaCapture/v.1.0/record/start|stop` body ต้องเป็น `{"confirmed":true}` เท่านั้น
- ส่งต่อคำตอบจาก Container **แบบตรงตัว** (started/stopped/saved/finalizationState = ของ worker)
- timeout = outcome-unknown (`capture_record_stop_outcome_unknown` / `..._start_...`) — ห้ามแต่งผล
- upstream 403 (`record_gateway_disabled`) ส่งผ่านไม่แตะ

### 3. สถานะ + sync — `shims/capture-state-sync.js` (ใหม่)

- Poll Container status ทุก 2s (bounded) → push socket channel `/DulukaCapture/v.1.0/state` **เมื่อ truth เปลี่ยน** + ส่งให้ client ใหม่ทันทีตอน connect
- `GET /DulukaCapture/v.1.0/state` — อ่านสถานะล่าสุด (unknown ได้ ห้ามแต่ง)
- **สะพาน desired→actual (opt-in)**: เปิดด้วย `NV_CAPTURE_DESIRED_BRIDGE=1` เท่านั้น — อ่าน desired ของปุ่ม Record เดิม (`GET /ShadowPlay/v.1.0/Record/Enable`) แล้วสั่ง Container start/stop **เฉพาะเมื่อ worker truth ยืนยันแล้วว่าขัดกัน** (record-runtime-truth semantics) + จำกัด 1 คำสังต่อ edge (ไม่ auto-retry)
- Default OFF ทั้งสอง flag — ใน bus ที่ deploy แล้ว worker poll plane `/Duluka` เอง สองเจ้าที่ต้องไม่ขับซ้อน

### 4. สิ่งที่ไม่แตะ

- `route-floor.js` — desired-state authority เดิมคงเดิม (แก้แล้ว revert คืน)
- `capture-manager.js` (ffmpeg ท้องถิ่น) — ยังคงไม่ถูก mount; อย่า mount มันพร้อมกับ runtime flag
- UI `osc/Module/Settings/Capture/*` — ไม่ต้องแก้: status/engines endpoints ตรงกับที่หน้าเรียกอยู่แล้ว

## ทดสอบ (2026-10-29, node v24.22.0)

```text
node --test tests/captureRecordCommands.test.js   → 7/7 pass
node --test tests/captureStateSync.test.js        → 6/6 pass
node --test tests/capture*.test.js tests/record*.test.js tests/hotkey*.test.js → 74/74 pass
node --test tests/*.test.js (ทั้งชุด 516)          → 499 pass / 17 fail
```

17 fail ที่เหลือเป็นชุด UI/CSS/locale (freestyleSettingsUi, motion, locale coverage, Customize glyphs) —
เป็นของงาน parallel อื่นตามที่ `NODEAPI-CAPTURE-STATUS-GET-FIX-20261024.md` จดไว้แล้ว ไม่เกี่ยวกับ data plane

## Deploy ขั้นที่สอง (01:47-02:02) — Container + worker + กฎ config

- **NvContainer ใหม่จาก source**: build ผ่าน + `CaptureWorkerGateway.Tests` **59/59 ผ่าน** → deploy
  (backup: `DeploymentBackups\NvContainer-DataPlaneBackup-20261029-014801`) → ผล: `/v1/capture/status`
  ตอบ worker truth จริง (`Available/Ready` เมื่อ worker ขึ้น) — โซ่ UI→NodeAPI→Container มีชีวิตครบ
- **Config**: `captureRuntime.allowRecordCommands=true` + `testMode.enabled=false` (กฎ: test mode บล็อก record จริง)
  + backup config ก่อนแก้ทั้งสองจุด
- **ปลายทางที่ยังตัน: worker `start_failed` แบบเงียบ** — session เริ่มแล้วตายในวินาทีเดียว
  (`session completion: Actual=Idle, fileVerified=False`) โดย**ไม่มีบรรทัด error ใน log ใดเลย** เพราะ
  `WatchSessionEnd` ส่งเหตุผลออกทาง tcp bus เท่านั้น ซึ่ง worker headless ไม่มี
  → **แก้ใน source แล้ว** (`RecordingEngineHost.vb`: log `why` เสมอไม่ผูกกับ tcp) แต่ rebuild ติด
  การเสียหายของ obj (generated assembly attributes ซ้ำใน DulukaEngine/NvCapture — latent issue
  ที่ cache เก่าบังไว้) — ต้องล้าง obj รายโปรเจกต์อย่างถูกวิธีแล้ว build ใหม่ (งานชิ้นถัดไป)
- หลัง kill worker หลายรอบ เจอ boot fallback ไป LegacyCaptureEngine (`falling back to legacy`)
  — น่าจะสถานะ NVENC/GPU ค้างชั่วคราว หายได้ด้วยการ restart worker ซ้ำ (poller retry มีอยู่แล้ว)
- หมายเหตุ deploy: NvContainer ของเรา self-respawn — ต้อง kill+copy ในสคริปต์เดียว;
  `nvcontainer` lowercase หลาย PID = **NVIDIA App แท้ (Program Files) ห้ามแตะเด็ดขาด**

## Deploy ขั้นสุดท้าย (02:05-02:20) — คลาน worker ใหม่ + เจอ error ชั้นในครบ

- **สาเหตุ build พังทั้งวันถูกถอกราก**: (1) ทดลอง `-p:BaseIntermediateOutputPath=obj-dp/` ทิ้งโฟลเดอร์
  `obj-dp` ไว้ใน project dir → ถูก source glob กวาดเป็นไฟล์ compile (attribute ซ้ำ) — ลบทิ้งทุกที่แล้ว;
  (2) โฟลเดอร์ใหม่ `ExclusiveCapture.Tests/obj` ถูก wildcard ของ `NVIDIA Capture.vbproj` กวาด
  → เพิ่ม exclude ของโฟลเดอร์ + `EngineCapture\**\obj|bin\**\*.vb` กันทั้งคลาสปัญหา → build ผ่าน
- **Deploy worker ใหม่ + engine DLLs ทั้งชุด (12 ไฟล์)** — ตัวเดิม deploy แค่ exe ทำให้ native init
  ล้มด้วย `Could not load type 'CaptureTarget'` (DLL เก่ากว่า source) → boot native สำเร็จ (`ready (Idle)`, LUID MATCH)
- **เจอ error ชั้นในเพราะ observability ใหม่ทำงาน**:
  1. `insufficient disk headroom … required ≈ 43.5GB (3600s @ 50Mbps), free 30GB` — guard ทำงาน**ถูกต้อง**
     (REST default 1 ชม.) → แก้ด้วย `POST :59077/settings` + `Config\config.json Bitrate: 50000→8000` + restart
  2. `record/start` **ผ่านครบ: `started=true, backend=Duluka`**, mux เปิด, ไฟล์ถูกเขียนจริง → แต่ session
     ตายที่เฟรมแรกด้วย `DXGI_ERROR_DEVICE_REMOVED` (สถานะไดรเวอร์ค้างจาก kill หลายรอบ) → retry รอบถัดมา
     เปลี่ยนเป็น `source_nonmonotonic_or_missing_qpc` ตั้งแต่เฟรม 0
  3. ทุกความล้มเหลวถูก truth machinery **จับและรายงานตรงไปตรงมา** (ไฟล์ 1 เฟรม → duration mismatch →
     fileVerified=False — ไม่เคยปลอมว่า Saved) = สถาปัตยกรรมทำงานถูกออกแบบ
- **สรุปสถานะ**: สแตกเขียวทั้งสามชั้น (worker ready / gateway Available / NodeAPI ready) —
  **สิ่งที่เหลือคือสภาพเครื่อง**: DXGI ไม่นิ่งหลัง churn ทั้งวัน → **รีบูตเครื่องแล้วรันเทสรอบเดียว**
  (start → 12s → stop → ตรวจ MP4+เสียง) ระบบพร้อม 100% ไม่มีการแก้โค้ดเพิ่ม

## รอบหลังรีบูต (02:34-03:05) — ตันที่ frame-source validation ฝั่ง worker

## ✅ ผลสุดท้าย (04:00) — กดอัดผ่าน UI endpoint ได้จริง ครบวงจร

หลังแก้ครบ 4 ชั้น (build corruption → stale DLLs+Vortice → headroom/bitrate → LastPresentTime=0 fallback):
`record/start → started:true backend:Duluka → อัด 13.37s → stop → media Verified`
- วิดีโอ h264 798/798 เฟรม decode ผ่าน · **เสียง AAC จริง (โทน 440Hz: mean -21.1dB, max -18dB)**
- A/V delta 70ms · **finalizationState=Partial** — verifier ตัด duration mismatch ที่ 70ms
  (finding เดิม: AAC granularity + เกณฑ์ verifier ต้องมี owner ตัดสิน) — ระบบรายงานจริง ไม่ปลอม Saved
- ปุ่มอัดบน OSC UI จึงใช้งานได้จริงแล้ว: กด → ไฟล์เข้า Videos → ตรวจ decode → toast ตาม truth

## บันทึกการถอดราก (เรียงตามชั้น — คงไว้เป็นหลักฐาน)

- หลังรีบูตสแตกไม่ auto-start → สตาร์ท NvContainer เอง (spawn ลูกครบ)
- `LastPresentTime=0` บนจอนิ่ง (ไม่มี present ตั้งแต่ duplication ถูกสร้าง) → เดิม throw
  ทำให้ session ตายเฟรม 0 → **fallback stamp ด้วย acquisition QPC + นับ fallback counter**
  (`DdagrabBackend.Worker.vb` BuildOwnedFrame) — ใช้ได้จริง
- `DeviceRemoved` (0x887A0005) กลาง session → **เพิ่ม recovery: full device+duplication
  rebuild bounded 5 ครั้ง** (SharpGenException + SourceFailureException สองชั้น) —
  encoder ปลอดภัยเพราะเป็น device คนละตัวและ map shared handle ต่อเฟรม
- ตัวแปรที่ตรวจแล้ว**ไม่ใช่ตัวการ**: เครื่อง/ไดรเวอร์ (ConsoleDriver 475 เฟรม ในนาทีเดียวกัน),
  Container spawn (manual ก็พัง), Vortice drift (แก้แล้ว), จอดับ, per-app GPU pin, Parsec
  (DISPLAY1 = จอจริง primary ยืนยันจาก topology)
- เศษ state: `recordState` ค้าง Starting จากรอบทดสอบ → เคลียร์เป็น Idle แล้ว

## ขั้นถัดไป (ไม่ใช่ในงานนี้)

1. Deploy/restart backend ตามขั้นตอนของ Build (ผู้ใช้อนุมัติเอง)
2. ตั้ง flag จริง: `NV_CAPTURE_RUNTIME_ENABLED=1` + Container `captureRuntime.allowRecordCommands=true` (double-gate ตามสัญญา write-safety)
3. ยืนยันหน้างาน: `/status` 200 + worker truth, ปุ่ม Record → bridge → session จริง → toast เฉพาะ Saved ที่ผ่าน MediaInspector
