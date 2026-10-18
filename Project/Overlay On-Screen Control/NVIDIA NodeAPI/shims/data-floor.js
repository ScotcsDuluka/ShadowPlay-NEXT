// data-floor.js — ชั้นข้อมูลจริงของ OSC (SELF-CONTAINED — ไม่ require ภายนอก ทนการย้ายโฟลเดอร์)
// ค่าทั้งหมด = shape จริงจาก Backend/lib/defaults.js + floor-defaults.json (proven on Intel floor)
// ต้นฉบับอ้างอิง: Project\Close Project\NVIDIA Web Helper.exe\Backend\lib\
'use strict';

const RES = ['In-game', '2160p 4K', '1440p HD', '1080p HD', '720p HD', '480p', '360p', '240p'];
const FPS = [60, 30];
const BITRATE_RECORD = { min: 10000000, max: 130000000, default: 50000000 };
const BITRATE_BROADCAST = { min: 1000000, max: 40000000, default: 3500000 };

const HOTKEY_DEFAULTS = {
  overlaytoggle: [18, 90], openshare: [18, 90],
  screenshot: [18, 112], nvcameraui: [18, 113],
  modsui: [18, 114], modstoggle: [16, 18, 114],
  modspreset1: [18, 116], modspreset2: [18, 117], modspreset3: [18, 118],
  modspresetcycle: [18, 115],
  broadcasttoggle: [18, 119], broadcastpausetoggle: [16, 18, 119],
  dvrtoggle: [16, 18, 121], recordtoggle: [18, 120], recordsave: [18, 121],
  cameratoggle: [18, 67], mictoggle: [18, 77],
  fps: [18, 80], ptt: [86], commentstoggle: [18, 88],
  overlayaswitch: [18, 65], overlaybswitch: [18, 66], overlaycswitch: [16, 18, 65],
  pmocoverlay: [18, 82], pmocoverlaycycle: [16, 18, 82],
  pmocresetaveragemetrics: [16, 18, 80], pmocloggingtoggle: [16, 18, 76]
};

const SETTINGS_RECORD = { quality: 'Custom', resolution: '1440p HD', framerate: 60, bitrateBps: BITRATE_RECORD.default };
const SETTINGS_IR = { quality: 'Custom', resolution: '1440p HD', framerate: 60, bitrateBps: BITRATE_RECORD.default, replayLengthSeconds: 15 };
const SETTINGS_BROADCAST = { quality: 'Good', resolution: '720p HD', framerate: 30, bitrateBps: BITRATE_BROADCAST.default, provider: 'YTL' };

const customize = function (bit, extra) {
  const c = {
    resolutions: RES, framerates: FPS,
    quality: 'Custom', resolution: '1440p HD', framerate: 60,
    bitrate: { current: bit.default, min: bit.min, max: bit.max },
  };
  for (var k in (extra || {})) c[k] = extra[k];
  return c;
};

// key = ชื่อ api.<Fn> ที่ Nv*API.js เรียก (จาก route handlers ใน NvShadowPlayAPI.js)
const FLOOR = {
  // ----- settings + customize (หน้า Settings/Record/IR/Broadcast) -----
  GetManualRecordCustomizeData: customize(BITRATE_RECORD),
  GetInstantReplayCustomizeData: customize(BITRATE_RECORD, { replayLengthSeconds: 15 }),
  GetBroadcastCustomizeData: customize(BITRATE_BROADCAST, { provider: 'YTL' }),
  GetManualRecordSettings: SETTINGS_RECORD,
  GetInstantReplaySettings: SETTINGS_IR,
  GetBroadcastSettings: SETTINGS_BROADCAST,

  // ----- states (สถานะอัด/IR/broadcast — idle) -----
  GetManualRecordStatus: { status: false },
  GetInstantReplayStatus: { status: false },
  GetInstantReplayRunning: { running: false },
  GetCaptureState: { captureMode: 0, recordingState: 0 },
  CaptureState: { captureMode: 0, recordingState: 0 },
  GetBroadcastStatus: { status: 'Stopped' },
  GetBroadcastLastPortal: { portal: '' },
  GetBroadcastTitle: { title: '' },
  Get4KSupport: { support: true },
  CustomOverlaySupport: { support: 'multiple' },

  // ----- concurrency -----
  ManualRecordConcurrencySupport: { supported: false },
  GetBroadcastSupport: { support: true },
  Broadcast2KSupport: { support: false },

  // ----- dropdown data -----
  GetResolutions: { resolutions: RES },
  GetFramerates: { framerates: FPS },
  // GET /ShadowPlay/v.1.0/BitRates/:quality/:resolution (route แท้ตอบ min/max/default — ไม่ใช่ array)
  GetBitrates: { bitrateBpsMin: BITRATE_RECORD.min, bitrateBpsMax: BITRATE_RECORD.max, bitrateBpsDefault: BITRATE_RECORD.default },

  // ----- HDR / display / capture -----
  GetHDRActiveStatus: { hdrActive: false },
  GetHDRState: { hdrState: false },
  GetDesktopCaptureSupport: { support: true },
  GetDesktopCaptureSupportReason: { support: true, unsupportReason: '' },
  DesktopCaptureEnable: { enabled: false },
  CaptureProcessInfo: {},
  CaptureState: { captureMode: 0, recordingState: 0 },

  // ----- indicator / webcam / mic / camera / nis / deepdvc -----
  GetFpsIndicatorSupport: { support: true },
  GetFpsIndicatorSettings: { state: false, position: 'top-right' },
  GetRecordIndicatorSupport: { support: true },
  GetRecordIndicatorSettings: { state: false },
  GetViewerIndicatorSupport: { support: true },
  GetMicrophonePresent: { present: true },
  GetWebcamPresent: { present: false },
  GetWebcamSettings: {},
  GetWebcamEnable: { enabled: false },
  GetNvCameraCompatible: { compatible: false },
  GetNis2State: { state: false },
  GetDeepDVCState: { state: false },
  Get8k60Support: { supported: false },
  GetCoPlayEnable: { enabled: false },

  // ----- osc home / screenshot / consent -----
  GetOSCMainViewData: {},
  GetScreenshotSupport: { support: true, unsupportReason: '' },
  GetFunctionalConsentState: { consent: true },
};

module.exports = { FLOOR, HOTKEY_DEFAULTS, RES, FPS, SETTINGS_RECORD, SETTINGS_IR, SETTINGS_BROADCAST };
