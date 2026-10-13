# OSC Data Contract — ค่าทั้งหมดที่ OSC ใช้ (เก็บจากของแท้ 2026-09-29)

แหล่ง: GFE 3.28 ติดตั้งจริง + NvNode แท้รันสด บนเครื่อง DESKTOP-DULUKA
ไฟล์ค่าดิบ: `genuine-harvest.txt` (คู่กัน)

## 1. Runtime model ของ NvNode แท้ (ต่างจากที่เราเคยทำ!)
- **port สุ่มใหม่ทุก boot** (เห็น 57645 → 57869 → 57127)
- **security cookie สุ่มใหม่ทุก boot** — `index.js:189` `const securityCookie = nvUtil.GenerateRandom(16);` (32 ตัวอักษร hex)
- คู่ {port, secret} ถูกเผยแพร่ผ่าน **Global MMF** `{8BA1E16C-FC54-4595-9782-E370A5FBE8DA}`
- หน้า OSC ได้ค่านี้จาก **cefQuery: QUERY_WIN_NODE_INFO** (host อ่าน MMF แล้วบอกหน้า)
- ส่ง cookie ทุก request: header `X_LOCAL_SECURITY_COOKIE` (หรือ query param สำหรับ socket.io handshake)
- → shim ของเราควรเลียนแบบ: สุ่ม port+cookie ต่อ boot + เขียน MMF + ตอบ WIN_NODE_INFO

## 2. cefQuery — คำสั่งทั้ง 10 ที่หน้า OSC ใช้จริง (นับจาก log รันจริง)
| คำสั่ง | ความถี่ | คือ/ตอบอะไร |
|---|---|---|
| QUERY_OSC_SET_PAINTING | 771 | บอก host ว่าหน้ากำลังวาด/หยุดวาด (สั่งเข้าโหมดประหยัด) |
| QUERY_WRITE_SHARED_STORAGE | 747 | เก็บค่า state ของหน้าลงดิสก์ (path: "osd-storage" ฯลฯ) |
| QUERY_OSC_SET_DISPLAY_RECTS | 391 | หน้าส่ง layout จอ/ตำแหน่ง OSC ให้ host |
| QUERY_FULLSCREEN_STATE | 388 | ถาม fullscreen/HDR/borderless → `{"fullscreen":false,"hdractive":false,"borderlessMode":false}` |
| QUERY_WIN_OPEN_OSC | 331 | สั่ง host เปิด overlay |
| QUERY_OSC_DISPLAY_IS_DESKTOP_MODE | 233 | ถามโหมดเดสก์ท็อป (จอคู่/single) |
| QUERY_WIN_CLOSE_OSC | 213 | สั่ง host ปิด overlay |
| QUERY_LOAD_STRING_TABLE | 59 | ขอตารางแปลภาษา |
| QUERY_WIN_NODE_INFO | 31 | ขอ {port, secret} ของ NvNode (จาก MMF) |
| QUERY_OSC_REGISTER_CLOSE_EVENT | 2 | ลงทะเบียน callback ปิด (persistent) |

## 3. NvNode API — ค่าจริงที่จับได้ (header X_LOCAL_SECURITY_COOKIE)
```
GET  /ShadowPlay/v.1.0/Record/Settings   → {"quality":"VeryGood","resolution":"In-game","framerate":60,"bitrateBps":50000000}
GET  /ShadowPlay/v.1.0/Record/Running    → {"running":false}
GET  /ShadowPlay/v.1.0/RecordPaths       → {"videos":"C:\\Users\\ScotcsDuluka\\Videos","tempFiles":"C:\\Users\\SCOTCS~1\\AppData\\Local\\Temp\\"}
GET  /ShadowPlay/v.1.0/Resolutions       → {"resolutions":["In-game","2160p 4K","1440p HD","1080p HD","720p HD","480p","360p","240p"]}
GET  /ShadowPlay/v.1.0/Framerates        → {"framerates":[60,30]}
GET  /ShadowPlay/v.1.0/Audio             → {"mode":"both"}
GET  /ShadowPlay/v.1.0/Broadcast/Support → {"support":true}
GET  /ShadowPlay/v.1.0/Broadcast/Settings→ {"quality":"Good","resolution":"720p HD","framerate":30,"bitrateBps":3500000,"provider":"Twitch"}
GET  /ShadowPlay/v.1.0/DesktopCapture/Support → {"support":true}
GET  /ShadowPlay/v.1.0/Screenshot/Support→ {"support":true}
GET  /ShadowPlay/v.1.0/4KSupport         → {"support":true}
GET  /ShadowPlay/v.1.0/GetHDRState       → {"active":false}
GET  /ShadowPlay/v.1.0/Webcam/Settings   → {"enable":false,"position":"RightBottom","size":"Small"}
POST /ShadowPlay/v.1.0/GetSupported {"language":"en-US"} → {"MediaPack":true}
POST /ShadowPlay/v.1.0/OSC/MainView {"fetchPartial":false} →
     {"webcamPresent":true,"webcamShown":false,"micPresentCount":3,"broadcastProvider":"AlwaysAsk",
      "coplayEnabled":false,"audioMode":"both","micMode":"alwayson",
      "instantReplayEnabled":false,"instantReplayRunning":false,"manualRecordEnabled":false}
POST /ShadowPlay/v1.0/OSC/GetCustomize/{Record|InstantReplay|Broadcast} {"quality","resolution"} → ตารางบิตเรต
```
- API ตอบ error สอน schema เอง: `"Argument doesn't have 'X' property"` → ใช้เป็น contract ตอนเขียน shim ได้เลย
- รายการ route ครบทั้ง ~75 เส้น อยู่ใน `C:\Program Files (x86)\NVIDIA Corporation\NvNode\NvShadowPlayAPI.js`

## 4. สถาปัตยกรรม Share.exe แท้ (จาก binary + log รันจริง)
- view = จอเป๊ะ (1680×1050), zoom = log(min(W/1920,H/1080))/log(1.2) → **-0.732395** บนจอนี้
- render: OSR + **D3D11** (offscreen_renderer_d3d11.cpp) + custom layer ผ่าน file mapping
- fallback "Invalid dirty rects → paint full layer" (มี string ใน binary)
- hotkey Alt+Z จดโดย Share เอง
- appdata log: %LOCALAPPDATA%\NVIDIA Corporation\NVIDIA Share\{debug.log,console.log}

## 5. นัยต่อระบบเรา
- **โหมด genuine** (mode:"genuine"): แม่ดูแล Share.exe แท้ — ใช้ค่าทั้งหมดข้างบนโดยธรรมชาติ
- **โหมด ours**: ถ้ากลับไปใช้ native host ของเรา — implement cefQuery ครบ 10 คำสั่ง + shim node
  เลียน random port/cookie + MMF (แทน fixed 59001) แล้ว UI/hover/ปุ่มจะทำงานเหมือนแท้
- capture engine เรา: bitrate/bounds จริงจากข้อ 3 (VeryGood=50Mbps, resolutions 8 ค่า, fps 60/30)

## 6. NvGraphics GPU runtime and preference events (schema v1)

NvGraphics sends only to the existing NvContainer loopback control API. NvContainer validates
each payload and publishes it through the existing ContainerEvents lane; event names are
fixed by NvContainer and are never accepted from a client.

### Runtime evidence

- Route: `POST /v1/gpu/runtime`
- Event: `/Duluka/v1/GpuRuntimeState`
- Required fields: `schemaVersion` (1), `eventId`, absolute `exePath`, `pid`, `state`,
  `adapters`, `engineTypes`, `windowStartUtc`, `windowDurationMs`, and `reason`.
- States: `Unknown`, `Inactive`, `Verified`, `MultipleAdapters`.
- `MultipleAdapters` means 3D activity was observed on multiple resolved physical adapters
  within the same counter sample. If different adapters appear only in different samples
  of the observation window, report `Unknown`: the evidence cannot distinguish sequential
  switching from simultaneous use.
- Adapter entries carry `name`, PnP `adapterId`, and `pciBusId` when known.
- The `adapters` array contains runtime evidence only. Windows preferences and preference
  events must never be used to infer runtime state. `Inactive` is not evidence that a game
  does not use a GPU.

### Applied preference

- Route: `POST /v1/gpu/preference`
- Event: `/Duluka/v1/GpuPreferenceApplied`
- Required fields: `schemaVersion` (1), `eventId`, `exePath`, and `appliedUtc`.
  `adapterId` is required unless `isSystemDefault` is `true`; for system-default events
  it may be empty. `isSystemDefault` is a boolean and defaults to `false`.
  `displayName` and `adapterName` are display metadata.

### Adapter inventory

- Route: `POST /v1/gpu/inventory`
- Event: `/Duluka/v1/GpuAdapterInventory`
- Required fields: `schemaVersion` (1), `eventId`, `capturedUtc`, and `adapters`.
- Each adapter may include its PnP identity, PCI bus id, resolved DXGI LUIDs, and
  nvidia-smi utilization/VRAM values. Missing telemetry remains null/empty; it is never
  inferred from runtime state or fabricated.
- Inventory payloads are capped at 32,768 bytes and expire after two minutes. Values are
  range-checked before NvContainer forwards them under this fixed event name.
- This event reports a verified per-app Windows preference write only. It does not assert
  that a running game is using that adapter.

### Validation and freshness

- Runtime payloads are capped at 65,536 bytes; preference payloads at 16,384 bytes.
- Both contracts reject unknown schema versions and timestamps more than two minutes in
  the past or future. Consumers should treat older retained runtime/preference state as
  stale after two minutes.
- The three GPU event names above are the complete GPU event allowlist for the current schema.
