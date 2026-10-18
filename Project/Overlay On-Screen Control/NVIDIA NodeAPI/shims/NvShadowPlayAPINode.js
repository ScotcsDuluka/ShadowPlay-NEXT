// NvShadowPlayAPINode.js — JS shim for the genuine native addon.
// Implements the ShadowPlay hotkey system (the native half):
//   SetHotkeyCallback(cb)  — the genuine module registers its fire callback
//   HotKey(...)            — hotkey registrations (name -> combo content)
//   HotKeyMonitor(...)     — monitor enable state
// The OS listener posts to the loopback fire endpoint. OpenShare is relayed
// as WindowState/overlayToggle so the OSC page owns its visibility state;
// other hotkeys use the regular /ShadowPlay/v.1.0/Hotkey event.
'use strict'

const http = require('http');
const path = require('path');
const make = require('./generic-addon.js');
const { HOTKEY_DEFAULTS } = require('./data-floor.js');
const state = require('./osc-runtime-state.js');
// (ตัด require defaults.js ที่ชี้โฟลเดอร์เก่า — โมเดลย้ายไป Close Project แล้ว
//  ข้อมูลทั้งหมด inline ใน data-floor.js แล้ว)

let hotkeyCallback = null;
let oscWindowStateCallback = null;   // the overlay open/close driver
let oscCaptureStateCallback = null;
const registered = {};   // name -> content (from HotKey registrations)
let monitorEnabled = false;
const hotkeyEventIds = {
  RecordToggle: 'Manual',
  RecordSave: 'DVR',
  InstantReplayToggle: 'DVRToggle',
  BroadcastToggle: 'GameCast',
  BroadcastPauseToggle: 'PauseResume',
};

// ---- settings persistence (OSC-DATA-CONTRACT §3: ค่า seed จากของแท้) ----
const fs = require('fs');
const SETTINGS_FILE = process.env.NV_SHADOWPLAY_SETTINGS_FILE ||
  path.join(__dirname, '..', 'shadowplay-settings.json');
const SETTINGS_DEFAULTS = {
  record:       { quality: 'VeryGood', resolution: 'In-game', framerate: 60, bitrateBps: 50000000 },
  audio:        { mode: 'both' },
  broadcast:    { quality: 'Good', resolution: '720p HD', framerate: 30, bitrateBps: 3500000, provider: 'Twitch' },
  webcam:       { enable: false, position: 'RightBottom', size: 'Small' },
  recordpaths:  { videos: 'C:\\Users\\ScotcsDuluka\\Videos', tempFiles: 'C:\\Users\\SCOTCS~1\\AppData\\Local\\Temp\\' },
  customoverlay:{ enable: false, path: '' },
};
function loadSettings() {
  try {
    const stored = JSON.parse(fs.readFileSync(SETTINGS_FILE, 'utf8'));
    if (!stored || typeof stored !== 'object' || Array.isArray(stored)) {
      throw new Error('OSC settings must contain a JSON object');
    }
    return Object.assign({}, SETTINGS_DEFAULTS, stored);
  } catch (e) {
    if (e.code === 'ENOENT') return JSON.parse(JSON.stringify(SETTINGS_DEFAULTS));
    throw e;
  }
}
function saveSettings(s) {
  const temp = SETTINGS_FILE + '.' + process.pid + '.' + Date.now() + '.tmp';
  try {
    fs.mkdirSync(path.dirname(SETTINGS_FILE), { recursive: true });
    fs.writeFileSync(temp, JSON.stringify(s, null, 2));
    fs.renameSync(temp, SETTINGS_FILE);
  } catch (e) {
    try { fs.unlinkSync(temp); } catch (_) {}
    throw e;
  }
}

function replyWithSettings(doReply, select) {
  let result;
  try {
    result = select(loadSettings());
  } catch (e) {
    if (typeof doReply === 'function') doReply(e);
    else throw e;
    return;
  }
  if (typeof doReply === 'function') doReply(null, result);
  return result;
}

function updateSettings(doReply, update, afterSave) {
  let result;
  try {
    const settings = loadSettings();
    result = update(settings);
    saveSettings(settings);
    if (typeof afterSave === 'function') afterSave(result);
  } catch (e) {
    console.error('[shim:settings] save failed: ' + e.message);
    if (typeof doReply === 'function') doReply(e);
    else throw e;
    return;
  }
  if (typeof doReply === 'function') doReply(null, result);
  return result;
}

function fire(name) {
  const data = Object.assign({ hotKeyName: name }, registered[name] || {}, {
    hotkeyId: hotkeyEventIds[name] || name,
  });
  const isOpenShare = String(name).toLowerCase() === 'openshare';
  try {
    console.log('[shim:hotkey] firing ' + name);
    if (isOpenShare) {
      if (!oscWindowStateCallback) {
        console.error('[shim:hotkey] OpenShare window-state callback is not ready');
        return false;
      }
      oscWindowStateCallback({ windowMsg: 'overlayToggle' });
      return true;
    }
    // OpenShare uses only WindowState; sending a Hotkey event as well makes the
    // page process the same toggle through two independent socket channels.
    if (typeof hotkeyCallback !== 'function') {
      console.error('[shim:hotkey] Hotkey callback is not ready for ' + name);
      return false;
    }
    hotkeyCallback(data);
    return true;
  } catch (e) {
    try { console.error('[shim:hotkey] fire failed: ' + e.message); } catch (e2) {}
    return false;
  }
}

// OS-side stand-in: fire handler ย้ายไปพอร์ตเดียวกับ node (:59011) —
// index.js ประกาศ route /?hk=<Name> เรียก global.__nvFire (แทน fireServer :59012 เดิม)
global.__nvFire = fire;

const specific = {
  SetHotkeyCallback: function (cb) { hotkeyCallback = cb; },
  // ---- settings: เก็บ/อ่านจริง (signature ตาม NvShadowPlayAPI.js ของแท้) ----
  GetManualRecordSettings: function (doReply) {
    return replyWithSettings(doReply, function (s) { return s.record; });
  },
  SetManualRecordSettings: function (doReply, content) {
    return updateSettings(doReply, function (s) {
      if (content && typeof content === 'object') s.record = Object.assign({}, s.record, content);
      return s.record;
    });
  },
  GetBroadcastSettings: function (doReply) {
    return replyWithSettings(doReply, function (s) { return s.broadcast; });
  },
  SetBroadcastSettings: function (doReply, content) {
    return updateSettings(doReply, function (s) {
      if (content && typeof content === 'object') s.broadcast = Object.assign({}, s.broadcast, content);
      return s.broadcast;
    });
  },
  AudioMode: function (doReply, enable, content) {
    // แท้: AudioMode(doReply, true, content) ตอน set / (doReply, false) ตอน read
    if (enable && content && typeof content === 'object') {
      return updateSettings(doReply, function (s) {
        s.audio = Object.assign({}, s.audio, content);
        return s.audio;
      });
    }
    return replyWithSettings(doReply, function (s) { return s.audio; });
  },
  GetWebcamOverlaySettings: function (doReply) {
    return replyWithSettings(doReply, function (s) { return s.webcam; });
  },
  WebcamOverlaySettings: function (doReply, content) {
    return updateSettings(doReply, function (s) {
      if (content && typeof content === 'object') s.webcam = Object.assign({}, s.webcam, content);
      return s.webcam;
    });
  },
  RecordingPaths: function (doReply, set, content) {
    if (set && content && typeof content === 'object') {
      return updateSettings(doReply, function (s) {
        s.recordpaths = Object.assign({}, s.recordpaths, content);
        return s.recordpaths;
      });
    }
    return replyWithSettings(doReply, function (s) { return s.recordpaths; });
  },
  CustomOverlayPath: function (doReply, set, content) {
    if (set && content && typeof content === 'object') {
      return updateSettings(doReply, function (s) {
        s.customoverlay = Object.assign(s.customoverlay, content);
        return s.customoverlay;
      });
    }
    return replyWithSettings(doReply, function (s) { return s.customoverlay; });
  },
  SetOscWindowStateChangeNotificationCallback: function (cb) {
    // The overlay open/close driver: the genuine native fires this and the
    // module relays it as the /ShadowPlay/v.1.0/WindowState socket event.
    oscWindowStateCallback = cb;
  },
  SetOscCaptureStateChangeNotificationCallback: function (cb) {
    oscCaptureStateCallback = cb;
  },
  GetDesktopCaptureSupportReason: function (doReply) {
    // Our engine IS the desktop-capture path — declare full support so the
    // record UI gates open (the page crashes on unsupportReason=undefined).
    if (typeof doReply === 'function') doReply(null, { support: true, unsupportReason: '' });
  },
  GetCaptureState: function (doReply) {
    if (typeof doReply === 'function') doReply(null, { captureMode: 0, recordingState: 0 });
  },
  // GET /ShadowPlay/v.1.0/Resolutions/:quality + /Framerates/:quality land here.
  // The page maps quality id -> tier label (Low->"Average" ... ) and reads
  // data.resolution / data.framerate; undefined here is the app.js TypeError
  // "Cannot read property 'name' of undefined" (proven in console.log 3926-09-28).
  GetQualityDefaultData: function (doReply, quality, type) {
    const RES_BY_TIER = {
      Average: '1080p HD', Good: '1440p HD', VeryGood: '1440p HD',
      UltraGood: '2160p 4K', Custom: '1440p HD',
    };
    const FPS_BY_TIER = { Average: 60, Good: 60, VeryGood: 60, UltraGood: 60, Custom: 60 };
    const tier = String(quality || '').replace(/\/+$/, '') || 'Custom';
    if (type === 'framerate') {
      if (typeof doReply === 'function') doReply(null, { framerate: FPS_BY_TIER[tier] || 60 });
    } else {
      if (typeof doReply === 'function') doReply(null, { resolution: RES_BY_TIER[tier] || '1440p HD' });
    }
  },
  GetShadowPlayStatus: function (doReply) {
    if (typeof doReply === 'function') doReply(null, { launch: true });
  },
  // hotkey defaults (ค่า GFE แท้): Alt+Z / F1 screenshot / F9 record / F10 IR / F8 broadcast
  GetHotKey: function (doReply, name) {
    try {
      const key = String(name || '').toLowerCase();
      const settings = loadSettings();
      const keys = (settings.hotkeys && settings.hotkeys[key]) ||
        HOTKEY_DEFAULTS[key] || [];
      if (typeof doReply === 'function') doReply(null, { keys: keys });
      return { keys: keys };
    } catch (e) {
      if (typeof doReply === 'function') doReply(e);
      else throw e;
    }
  },
  GetOSCMainViewData: function (doReply, content) {
    const settings = loadSettings();
    if (typeof doReply === 'function') doReply(null, {
      instantReplayEnabled: state.irEnabled,
      instantReplayRunning: state.irRunning,
      manualRecordEnabled: state.recordRunning,
      broadcastProvider: (settings.broadcastPreference && settings.broadcastPreference.provider) || 'AlwaysAsk',
      webcamPresent: state.webcamPresent,
      webcamShown: state.webcamEnable,
      micPresentCount: state.micPresent ? 1 : 0,
      micMode: (settings.microphone && settings.microphone.mode) || 'off',
      audioMode: (settings.audio && settings.audio.mode) || 'both',
      coplayEnabled: state.coplay,
    });
  },
  HotKey: function (doReply, enable, name, content) {
    const key = String(name || '').toLowerCase();
    if (enable) {
      const keys = Array.isArray(content) ? content :
        (content && Array.isArray(content.keys) ? content.keys : null);
      if (keys) {
        return updateSettings(doReply, function (settings) {
          settings.hotkeys = settings.hotkeys || {};
          settings.hotkeys[key] = keys;
          return {};
        }, function () { registered[name] = content || {}; });
      }
      registered[name] = content || {};
    } else {
      // read: คืนค่า hotkey ที่หน้าตั้งไว้ หรือค่า default GFE
      try {
        const settings = loadSettings();
        const saved = settings.hotkeys && settings.hotkeys[key];
        const current = registered[name];
        const keys = saved || (current && current.keys) || HOTKEY_DEFAULTS[key] || [];
        if (typeof doReply === 'function') doReply(null, { keys: keys });
        return { keys: keys };
      } catch (e) {
        if (typeof doReply === 'function') doReply(e);
        else throw e;
        return;
      }
    }
    // The overlay toggle hotkey fires the WINDOW STATE change (the genuine
    // native -> WindowStateChangeNotificationCallback -> /ShadowPlay/v.1.0/
    // WindowState socket event -> the page toggles itself). windowMsg=2 is
    // the toggle value observed on the wire.
    if (enable && name === 'toggle' && oscWindowStateCallback) {
      try { oscWindowStateCallback({ windowMsg: 'overlayToggle' }); }
      catch (e) { try { console.error('[shim] window state fire failed: ' + e.message); } catch (e2) {} }
    }
    if (typeof doReply === 'function') doReply(null, {});
  },
  HotKeyMonitor: function (doReply, enable, content) {
    monitorEnabled = !!enable;
    if (typeof doReply === 'function') doReply(null, {});
  },
  HotKeyDynamicEnable: function (doReply, content) {
    if (typeof doReply === 'function') doReply(null, {});
  },
  GetHotKeyMonitor: function (doReply) {
    if (typeof doReply === 'function') doReply(null, { enable: monitorEnabled });
  },
};

// recorder mode (DULUKA_RECORD=1) delegates to the genuine native addon
// (NvShadowPlayAPINode.node beside index.js) and logs every real reply.
module.exports = make(specific, null, path.join(__dirname, '..', 'NvShadowPlayAPINode.node'));
