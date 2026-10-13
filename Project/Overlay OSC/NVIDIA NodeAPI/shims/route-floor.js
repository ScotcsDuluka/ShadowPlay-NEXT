// route-floor.js — OSC API facade with response shapes and local runtime state.
// Hardware-dependent features still require their real backend/device support.
// mount: require('./shims/route-floor.js')(app);  — วางก่อน module routes
'use strict';

const HOTKEYS = {
  openshare: [18, 90],
  screenshot: [18, 112],
  recordtoggle: [18, 120],
  recordsave: [18, 121],
  dvrtoggle: [16, 18, 121],
  instantreplaytoggle: [18, 117],
  broadcasttoggle: [18, 119],
  broadcastpausetoggle: [16, 18, 119],
  cameratoggle: [18, 67],
  mictoggle: [18, 77],
  fps: [18, 80],
  ptt: [86],
  commentstoggle: [18, 88],
  overlayaswitch: [18, 65],
  overlaybswitch: [18, 66],
  overlaycswitch: [16, 18, 65],
  pmocoverlay: [18, 82],
  pmocoverlaycycle: [16, 18, 82],
  pmocresetaveragemetrics: [16, 18, 80],
  pmocloggingtoggle: [16, 18, 76],
  modsui: [18, 114],
  modstoggle: [16, 18, 114],
  modspreset1: [18, 116],
  modspreset2: [18, 117],
  modspreset3: [18, 118],
  modspresetcycle: [18, 115],
  nvcameraui: [18, 113],
};

const RES = ['In-game', '2160p 4K', '1440p HD', '1080p HD', '720p HD', '480p', '360p', '240p'];
const FPS = [60, 30];

// ---- settings persistence (ไฟล์เดียวกับ NvShadowPlayAPINode shim — seed จากของแท้) ----
const fs = require('fs');
const path = require('path');
const http = require('http');
const SETTINGS_FILE = process.env.NV_SHADOWPLAY_SETTINGS_FILE ||
  path.join(__dirname, '..', 'shadowplay-settings.json');
const SETTINGS_DEFAULTS = {
  record:       { quality: 'VeryGood', resolution: 'In-game', framerate: 60, bitrateBps: 50000000 },
  instantReplay:{ quality: 'Custom', resolution: '1440p HD', framerate: 60, bitrateBps: 50000000, replayLengthSeconds: 15 },
  broadcast:    { quality: 'Good', resolution: '720p HD', framerate: 30, bitrateBps: 3500000, provider: 'Twitch' },
  audio:        { mode: 'both' },
  audioSettings:{ systemVolumePercent: 100, separateTracks: false },
  microphone:   { mode: 'off' },
  microphoneSettings: { '0': { index: 0, name: 'Default microphone', id: '', muted: false, volumePercent: 100, boostPercent: 0 } },
  webcam:       { enable: false, position: 'RightBottom', size: 'Small' },
  desktopCapture: false,
  recordpaths:  { videos: 'C:\\Users\\ScotcsDuluka\\Videos', tempFiles: 'C:\\Users\\SCOTCS~1\\AppData\\Local\\Temp\\' },
  broadcastPreference: { provider: 'AlwaysAsk' },
  customoverlay:{ enable: false, path: '', slots: {} },
  notifier:     { slotCount: 2 },
  obs:          { enabled: false },
  indicators:   { fps: { state: false, position: 'top-right' }, record: { state: false }, viewer: { state: false } },
  highlights:   { enabled: false, sizeMB: 5120, tempSaveFolder: '' },
  language:     { language: 'en-US' },
};
function loadSettings() {
  try {
    const stored = JSON.parse(fs.readFileSync(SETTINGS_FILE, 'utf8'));
    const settings = Object.assign({}, SETTINGS_DEFAULTS, stored);
    Object.keys(SETTINGS_DEFAULTS).forEach(function (key) {
      const defaults = SETTINGS_DEFAULTS[key];
      if (defaults && typeof defaults === 'object' && !Array.isArray(defaults)) {
        settings[key] = Object.assign({}, defaults, stored[key] || {});
      }
    });
    settings.microphoneSettings = Object.assign({}, SETTINGS_DEFAULTS.microphoneSettings, stored.microphoneSettings || {});
    settings.customoverlay.slots = Object.assign({}, SETTINGS_DEFAULTS.customoverlay.slots,
      (stored.customoverlay && stored.customoverlay.slots) || {});
    if (!Number.isFinite(settings.audioSettings.systemVolumePercent)) {
      settings.audioSettings.systemVolumePercent = 100;
    }
    if (!settings.highlights.tempSaveFolder) {
      settings.highlights.tempSaveFolder = settings.recordpaths.videos;
    }
    return settings;
  }
  catch (e) {
    if (e.code !== 'ENOENT') throw e;
    const settings = JSON.parse(JSON.stringify(SETTINGS_DEFAULTS));
    settings.highlights.tempSaveFolder = settings.recordpaths.videos;
    return settings;
  }
}
function saveSettings(s) {
  writeJsonFileAtomic(SETTINGS_FILE, s);
}
function persistSettings(s, res) {
  try {
    saveSettings(s);
    return true;
  } catch (e) {
    console.error('[osc-backend] settings save failed: ' + e.message);
    res.status(500).json({ type: 'Error', code: -1, codeText: 'SETTINGS_SAVE_FAILED',
      message: 'Unable to persist OSC settings' });
    return false;
  }
}
function appRootCandidates() {
  const candidates = [];
  const configured = process.env.NVIDIA_SHADOWPLAY_APP_ROOT;
  if (configured) candidates.push(path.resolve(configured));
  [process.cwd(), __dirname].forEach(function (start) {
    let current = path.resolve(start);
    for (let depth = 0; depth < 10; depth += 1) {
      candidates.push(current);
      candidates.push(path.join(current, 'build', 'NVIDIA ShadowPlay'));
      const parent = path.dirname(current);
      if (parent === current) break;
      current = parent;
    }
  });
  return candidates;
}
function captureConfigPaths() {
  const root = appRootCandidates().find(function (candidate) {
    return fs.existsSync(path.join(candidate, 'NvContainer')) &&
      fs.existsSync(path.join(candidate, 'NvOverlay'));
  });
  if (!root) return null;
  const configDir = path.join(root, 'Config');
  return {
    config: path.join(configDir, 'config.json'),
    engine: path.join(configDir, 'engine.json')
  };
}
function readJsonFile(file, fallback) {
  try {
    return JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch (e) {
    if (e.code === 'ENOENT') return fallback;
    throw e;
  }
}
function writeJsonFileAtomic(file, value) {
  const temp = file + '.' + process.pid + '.' + Date.now() + '.tmp';
  try {
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(temp, JSON.stringify(value, null, 2));
    fs.renameSync(temp, file);
  } catch (e) {
    try { fs.unlinkSync(temp); } catch (_) {}
    throw e;
  }
}
function captureOptions(paths) {
  const config = readJsonFile(paths.config, {});
  const recording = config.Recording || config.recording || {};
  const nestedCurrent = recording.current || {};
  const engine = readJsonFile(paths.engine, {});
  const configuredApi = recording.APICapture || recording.api_capture || '';
  const captureMethod = String(configuredApi || engine.CaptureMethod || 'ddagrab').toLowerCase();
  const configuredMode = recording.EngineMode || recording.engine_mode;
  const engineMode = String(configuredMode ||
    (captureMethod === 'ddagrab' || captureMethod === 'dxgi_desktop_duplication' ? 'Duluka' : 'FFmpeg')).toLowerCase();
  if (engineMode !== 'duluka' && engineMode !== 'ddagrab' && engineMode !== 'ffmpeg' && engineMode !== 'legacy') {
    throw new Error('Unsupported capture engine mode: ' + engineMode);
  }
  const mode = engineMode === 'duluka' || engineMode === 'ddagrab' ? 'Duluka' : 'FFmpeg';
  let apiCapture = configuredApi || engine.CaptureMethod || 'ddagrab';
  if (mode === 'Duluka') {
    if (String(apiCapture).toLowerCase() !== 'ddagrab' &&
        String(apiCapture).toLowerCase() !== 'dxgi_desktop_duplication') {
      throw new Error('Unsupported Duluka capture API: ' + apiCapture);
    }
    apiCapture = 'dxgi_desktop_duplication';
  } else if (['ddagrab', 'gdigrab', 'gfxcapture'].indexOf(String(apiCapture).toLowerCase()) < 0) {
    throw new Error('Unsupported FFmpeg capture API: ' + apiCapture);
  }
  const encoder = recording.Encoder || recording.encoder || 'NVENC_H264';
  if (['NVENC_H264', 'NVENC_HEVC', 'NVENC_AV1', 'QuickSync_H264', 'QuickSync_HEVC',
    'AMF_H264', 'AMF_HEVC', 'LibX264', 'LibX265'].indexOf(encoder) < 0) {
    throw new Error('Unsupported encoder: ' + encoder);
  }
  const useNativeResolution = nestedCurrent.use_native_resolution !== undefined
    ? nestedCurrent.use_native_resolution === true
    : (recording.UseNativeResolution !== undefined ? recording.UseNativeResolution === true : true);
  const configuredQuality = recording.Preset || recording.preset;
  const quality = ['Low', 'Medium', 'High', 'Custom'].indexOf(configuredQuality) >= 0
    ? configuredQuality : 'Custom';
  return {
    engineMode: mode,
    quality: quality,
    apiCapture: apiCapture,
    encoder: encoder,
    encoderPreset: Number(nestedCurrent.encoder_preset || recording.EncoderPreset ||
      recording.encoder_preset || parseInt(String(engine.Preset || 'p4').replace(/^p/i, ''), 10) || 4),
    captureCursor: engine.CaptureCursor === true,
    fps: Number(nestedCurrent.fps || recording.FPS || recording.fps || 60),
    bitrateKbps: Number(nestedCurrent.bitrate || recording.Bitrate || recording.bitrate || 50000),
    useNativeResolution: useNativeResolution,
    width: Number(nestedCurrent.width || recording.Width || recording.width || 1920),
    height: Number(nestedCurrent.height || recording.Height || recording.height || 1080),
    replayLengthSeconds: Number(recording.replay_duration || recording.ReplayDuration || 60)
  };
}
function validateCaptureOptions(body) {
  if (!body || typeof body !== 'object' || Array.isArray(body)) return 'Capture options require a JSON object';
  if (body.engineMode !== 'FFmpeg' && body.engineMode !== 'Duluka') return 'engineMode must be FFmpeg or Duluka';
  if (body.quality !== undefined &&
      ['Low', 'Medium', 'High', 'Custom'].indexOf(body.quality) < 0) {
    return 'quality must be Low, Medium, High, or Custom';
  }
  if (!Number.isInteger(body.fps) || body.fps < 1 || body.fps > 800) {
    return 'fps must be an integer from 1 to 800';
  }
  if (!Number.isInteger(body.bitrateKbps) || body.bitrateKbps < 1 || body.bitrateKbps > 150000) {
    return 'bitrateKbps must be an integer from 1 to 150000';
  }
  if (typeof body.useNativeResolution !== 'boolean') return 'useNativeResolution must be a boolean';
  if (!Number.isInteger(body.width) || body.width < 1 || body.width > 16384 ||
      !Number.isInteger(body.height) || body.height < 1 || body.height > 16384) {
    return 'width and height must be integers from 1 to 16384';
  }
  const apiOptions = body.engineMode === 'Duluka'
    ? ['dxgi_desktop_duplication']
    : ['ddagrab', 'gdigrab', 'gfxcapture'];
  if (apiOptions.indexOf(body.apiCapture) < 0) return 'apiCapture is not supported for the selected engine';
  const encoders = ['NVENC_H264', 'NVENC_HEVC', 'NVENC_AV1', 'QuickSync_H264', 'QuickSync_HEVC',
    'AMF_H264', 'AMF_HEVC', 'LibX264', 'LibX265'];
  if (encoders.indexOf(body.encoder) < 0) return 'encoder is not supported';
  if (!Number.isInteger(body.encoderPreset) || body.encoderPreset < 1 || body.encoderPreset > 7) {
    return 'encoderPreset must be an integer from 1 to 7';
  }
  if (typeof body.captureCursor !== 'boolean') return 'captureCursor must be a boolean';
  return null;
}
// อ่าน body POST ทั้งก้อน → JSON (คืน null ถ้า parse ไม่ได้/ไม่มี body)
function readBody(req, done) {
  let raw = '';
  req.on('data', function (c) { raw += c; if (raw.length > 256 * 1024) req.destroy(); });
  req.on('end', function () {
    try { done(raw ? JSON.parse(raw) : null); } catch (e) { done(null); }
  });
}
// คีย์ที่ผู้ใช้ปรับเอง (ไฟล์ settings) มาก่อนค่า default
function savedHotkey(name) {
  const s = loadSettings();
  return (s.hotkeys && s.hotkeys[name]) || HOTKEYS[name] || [];
}

function isContainerTestRoute(req) {
  if (req.method !== 'GET' && req.method !== 'POST') return false;
  return /^\/ShadowPlay\/v\.1\.0\/(Record\/(Enable|Running|Save)|InstantReplay\/(Enable|Running|Save)|Broadcast\/(Enable|Running|Pause)|Screenshot\/(Support|Capture)|Capture\/State|OSC\/MainView)$/.test(req.path) ||
    /^\/NvCamera\/v\.1\.\d+(\/|$)/.test(req.path) ||
    /^\/Duluka\/v\.1\.0\/(State|Actual)$/.test(req.path);
}

function isContainerOwnedMenuRoute(req) {
  const path = req.path;
  return (req.method === 'GET' &&
      (path === '/NvCamera/v.1.0/Compatible' ||
       path === '/NvCamera/v.1.0/GetNvCameraConfig' ||
       path === '/ShadowPlay/v.1.0/Screenshot/Support')) ||
    (req.method === 'POST' &&
      (path === '/NvCamera/v.1.0/GetAvailable' ||
       path === '/NvCamera/v.1.0/GetFreestyleSupport' ||
       path === '/NvCamera/v.1.0/EnableMods'));
}

function containerControlPort() {
  const port = Number(process.env.NV_CONTAINER_CONTROL_API_PORT);
  return Number.isInteger(port) && port > 0 && port <= 65535 ? port : 59012;
}

function containerTestStatus(callback) {
  const port = containerControlPort();
  const request = http.get({ hostname: '127.0.0.1', port: port, path: '/v1/status', timeout: 1500 }, function (response) {
    let raw = '';
    response.setEncoding('utf8');
    response.on('data', function (chunk) { raw += chunk; });
    response.on('end', function () {
      try {
        const status = JSON.parse(raw);
        if (response.statusCode !== 200 || typeof status.testMode !== 'boolean') {
          return callback(new Error('NvContainer returned an invalid test-mode status'));
        }
        if (process.env.NV_CONTAINER_TEST_MODE === '1' && status.testMode !== true) {
          return callback(new Error('NvContainer test-mode configuration does not match NvNode'));
        }
        callback(null, status);
      } catch (e) {
        callback(new Error('NvContainer returned invalid test-mode status'));
      }
    });
  });
  request.on('timeout', function () { request.destroy(new Error('NvContainer test-mode status timed out')); });
  request.on('error', callback);
}

function sendContainerTestCommand(req, res, body, callback) {
  requestContainer('POST', '/v1/dispatch', {
    method: req.method,
    path: req.path,
    body: body
  }, callback);
}

function requestContainer(method, route, body, callback) {
  const port = containerControlPort();
  const payload = body === undefined ? null : Buffer.from(JSON.stringify(body));
  const request = http.request({
    hostname: '127.0.0.1',
    port: port,
    path: route,
    method: method,
    timeout: 3000,
    headers: payload
      ? { 'Content-Type': 'application/json', 'Content-Length': payload.length }
      : {}
  }, function (response) {
    let raw = '';
    response.setEncoding('utf8');
    response.on('data', function (chunk) { raw += chunk; });
    response.on('end', function () {
      let result;
      try { result = JSON.parse(raw); }
      catch (e) { return callback(new Error('NvContainer returned invalid test-command response')); }
      callback(null, result);
    });
  });
  request.on('timeout', function () { request.destroy(new Error('NvContainer test command timed out')); });
  request.on('error', callback);
  request.end(payload || undefined);
}

const state = require('./osc-runtime-state.js');
state.desktopCapture = !!loadSettings().desktopCapture;

function requiredBoolean(body, key) {
  return body && typeof body[key] === 'boolean' ? body[key] : null;
}

function settingsShape(kind) {
  const s = loadSettings();
  if (kind === 'instantreplay') return s.instantReplay;
  if (kind === 'broadcast') return s.broadcast;
  return s.record;
}

function mainViewData() {
  const settings = loadSettings();
  return {
    instantReplayEnabled: state.irEnabled,
    instantReplayRunning: state.irRunning,
    manualRecordEnabled: state.recordRunning,
    broadcastProvider: settings.broadcastPreference.provider,
    webcamPresent: state.webcamPresent,
    webcamShown: state.webcamEnable,
    micPresentCount: state.micPresent ? 1 : 0,
    micMode: settings.microphone.mode,
    audioMode: settings.audio.mode,
    coplayEnabled: state.coplay,
  };
}

module.exports = function routeFloor(app, io) {
  // ตัวอัด = NvCapture.exe (bus: NvContainer.exe spawn + watchdog)
  // หน้า/REST คุม recordEnabled → engine poll /Duluka plane เอง → Actual กลับมาที่นี่

  // NvContainer owns stable main-menu capability replies in every mode and
  // test-mode capture/game-filter replies before legacy routes can handle them.
  app.use(function (req, res, next) {
    const containerOwnedMenuRoute = isContainerOwnedMenuRoute(req);
    if (!containerOwnedMenuRoute && !isContainerTestRoute(req)) return next();
    containerTestStatus(function (statusError, status) {
      if (statusError) {
        console.error('[osc-backend] NvContainer test-mode status unavailable: ' + statusError.message);
        return res.status(503).json({ type: 'Error', code: -1, codeText: 'NVCONTAINER_UNAVAILABLE',
          message: 'NvContainer is required to handle this OSC request' });
      }
      if (!status.testMode && !containerOwnedMenuRoute) return next();

      const dispatch = function (body) {
        sendContainerTestCommand(req, res, body, function (dispatchError, result) {
          if (dispatchError) {
            console.error('[osc-backend] NvContainer test dispatch failed: ' + dispatchError.message);
            return res.status(503).json({ type: 'Error', code: -1, codeText: 'NVCONTAINER_DISPATCH_FAILED',
              message: 'NvContainer could not handle this OSC request' });
          }
          if (!result || result.handled !== true) {
            return res.status(502).json({ type: 'Error', code: -1, codeText: 'NVCONTAINER_UNHANDLED',
              message: 'NvContainer did not handle this OSC request' });
          }
          (result.events || []).forEach(function (event) {
            if (io && event && typeof event.name === 'string') {
              try { io.emit(event.name, event.data); }
              catch (e) { console.error('[osc-backend] test event delivery failed: ' + e.message); }
            }
          });
          res.status(result.statusCode || 200).json(result.body || {});
        });
      };
      if (req.method === 'POST') readBody(req, dispatch);
      else dispatch(null);
    });
  });

  // ★ แบบของแท้ (CaptureStateChangeNotificationCallback): state เปลี่ยน = emit
  //   ผ่าน socket.io ให้หน้าเห็นทันที ไม่ต้องรอ poll รอบถัดไป
  function emitCapture(name, data) {
    if (!io) return;
    try { io.emit(name, data); } catch (e) {}
  }

  app.use(function (req, res, next) {
    const m = req.method;
    const p = req.path;

    if (p === '/OBS/v.1.0/settings' || p === '/OBS/v.1.0/status' ||
        p === '/OBS/v.1.0/test') {
      const controlPath = p === '/OBS/v.1.0/settings' ? '/v1/obs/settings'
        : p === '/OBS/v.1.0/status' ? '/v1/obs/status'
          : '/v1/obs/test';
      const allowedMethod = p === '/OBS/v.1.0/settings' || p === '/OBS/v.1.0/status' ? ['GET', 'POST'] : ['POST'];
      if (allowedMethod.indexOf(m) < 0) {
        res.status(405).json({ type: 'Error', code: 4, codeText: 'METHOD_NOT_ALLOWED',
          message: 'Method not allowed for OBS control' });
        return;
      }
      const proxy = function (body) {
        requestContainer(m, controlPath, body, function (error, result) {
          if (error) {
            console.error('[osc-backend] NvContainer OBS request failed: ' + error.message);
            return res.status(503).json({ type: 'Error', code: -1, codeText: 'NVCONTAINER_UNAVAILABLE',
              message: 'NvContainer OBS runtime is unavailable' });
          }
          if (!result || result.handled !== true) {
            return res.status(502).json({ type: 'Error', code: -1, codeText: 'NVCONTAINER_UNHANDLED',
              message: 'NvContainer did not handle this OBS request' });
          }
          return res.status(result.statusCode || 200).json(result.body || {});
        });
      };
      if (m === 'POST') return readBody(req, proxy);
      return proxy(undefined);
    }

    // ---- POST desired state: retries must be safe and idempotent ----
    if (m === 'POST') {
      if (p === '/Notifier/v.1.0/settings') {
        readBody(req, function (body) {
          const slotCount = body && Number(body.slotCount);
          if (!Number.isInteger(slotCount) || slotCount < 1 || slotCount > 3) {
            res.status(400).json({ type: 'Error', code: 4, codeText: 'ET_INVALID_DATA',
              message: 'slotCount must be an integer from 1 to 3' });
            return;
          }
          const settings = loadSettings();
          settings.notifier = Object.assign({}, settings.notifier || {}, { slotCount: slotCount });
          if (!persistSettings(settings, res)) return;
          res.status(200).json(settings.notifier);
        });
        return;
      }
      if (p === '/DulukaCapture/v.1.0/settings') {
        readBody(req, function (body) {
          const validationError = validateCaptureOptions(body);
          if (validationError) {
            res.status(400).json({ type: 'Error', code: 4, codeText: 'ET_INVALID_DATA',
              message: validationError });
            return;
          }
          try {
            const paths = captureConfigPaths();
            if (!paths) {
              res.status(503).json({ type: 'Error', code: -1, codeText: 'CAPTURE_CONFIG_UNAVAILABLE',
                message: 'Unable to locate the shared NVIDIA ShadowPlay configuration directory' });
              return;
            }
            const config = readJsonFile(paths.config, {});
            const recordingKey = config.Recording ? 'Recording' : config.recording ? 'recording' : 'Recording';
            const recording = config[recordingKey] || {};
            if (body.quality !== undefined) recording.Preset = body.quality;
            const nested = recording.current && typeof recording.current === 'object';
            const isDuluka = body.engineMode === 'Duluka';
            const apiCapture = isDuluka ? 'ddagrab' : body.apiCapture;
            if (nested) {
              recording.engine_mode = body.engineMode;
              recording.api_capture = apiCapture;
              recording.encoder = body.encoder;
              recording.encoder_now = body.encoder;
              recording.current.encoder_preset = body.encoderPreset;
              recording.current.fps = body.fps;
              recording.current.bitrate = body.bitrateKbps;
              recording.current.use_native_resolution = body.useNativeResolution;
              recording.current.width = body.width;
              recording.current.height = body.height;
            } else {
              recording.EngineMode = body.engineMode;
              recording.APICapture = apiCapture;
              recording.Encoder = body.encoder;
              recording.EncoderNow = body.encoder;
              recording.EncoderPreset = body.encoderPreset;
              recording.FPS = body.fps;
              recording.Bitrate = body.bitrateKbps;
              recording.UseNativeResolution = body.useNativeResolution;
              recording.Width = body.width;
              recording.Height = body.height;
            }
            config[recordingKey] = recording;
            const engine = readJsonFile(paths.engine, {});
            engine.ConfigVersion = engine.ConfigVersion || 1;
            engine.CaptureMethod = apiCapture;
            engine.Preset = 'p' + body.encoderPreset;
            engine.CaptureCursor = body.captureCursor;
            writeJsonFileAtomic(paths.config, config);
            writeJsonFileAtomic(paths.engine, engine);
            res.status(200).json(captureOptions(paths));
          } catch (e) {
            console.error('[osc-backend] capture options save failed: ' + e.message);
            res.status(500).json({ type: 'Error', code: -1, codeText: 'CAPTURE_SETTINGS_SAVE_FAILED',
              message: 'Unable to persist capture engine settings' });
          }
        });
        return;
      }
      if (p === '/ShadowPlay/v.1.0/OSC/MainView') {
        readBody(req, function (body) {
          if (!body || typeof body !== 'object' || Array.isArray(body)) {
            res.status(400).json({ type: 'Error', code: 4, codeText: 'ET_INVALID_DATA',
              message: 'MainView requires a JSON object' });
            return;
          }
          res.status(200).json(mainViewData());
        });
        return;
      }
      // /Duluka plane: engine confirms actuals — authority for /Record/Running
      if (p === '/Duluka/v.1.0/Actual') {
        readBody(req, function (body) {
          const hasRecordState = body && typeof body.running === 'boolean';
          const hasIrState = body && typeof body.irRunning === 'boolean';
          const hasBroadcastState = body && typeof body.broadcastRunning === 'boolean';
          if (!hasRecordState && !hasIrState && !hasBroadcastState) {
            res.status(400).json({ type: 'Error', code: 4, codeText: 'ET_INVALID_DATA',
              message: 'Actual requires a boolean running, irRunning or broadcastRunning field' });
            return;
          }
          if (hasRecordState) {
            const changed = state.recordRunning !== body.running;
            state.recordRunning = body.running;
            // Engine-confirmed actual state is authoritative over the requested state.
            if (changed) {
              emitCapture('/ShadowPlay/v.1.0/Record/Enable', { status: body.running });
              emitCapture('/ShadowPlay/v.1.0/Record/Running', { running: body.running });
            }
          }
          if (hasIrState) {
            const changed = state.irRunning !== body.irRunning;
            state.irRunning = body.irRunning;
            if (changed) {
              emitCapture('/ShadowPlay/v.1.0/InstantReplay/Started', { started: body.irRunning });
            }
          }
          if (hasBroadcastState) {
            const changed = state.broadcastRunning !== body.broadcastRunning;
            state.broadcastRunning = body.broadcastRunning;
            if (changed) emitCapture('/ShadowPlay/v.1.0/Broadcast/Enable', { status: body.broadcastRunning });
          }
          res.status(200).json({});
        });
        return;
      }
      if (p === '/ShadowPlay/v.1.0/Record/Enable') {
        // This is a desired-state API, not a toggle: retries must not invert state.
        readBody(req, function (body) {
          const enabled = requiredBoolean(body, 'status');
          if (enabled === null) {
            res.status(400).json({ type: 'Error', code: 4, codeText: 'ET_INVALID_DATA',
              message: 'Record enable requires a boolean status field' });
            return;
          }
          const changed = state.recordEnabled !== enabled;
          state.recordEnabled = enabled;
          if (changed) emitCapture('/ShadowPlay/v.1.0/Record/Enable', { status: enabled });
          res.status(200).json({ status: enabled });
        });
        return;
      }
      if (p === '/ShadowPlay/v.1.0/InstantReplay/Save') {
        // สัญญาณ Saving หนึ่งจังหวะ → poller เห็น edge → engine save buffer
        state.irSaving = true;
        setTimeout(function () { state.irSaving = false; }, 1500);
        res.status(200).json({ status: true });
        return;
      }
      if (p === '/ShadowPlay/v.1.0/InstantReplay/Enable') {
        readBody(req, function (body) {
          const enabled = requiredBoolean(body, 'status');
          if (enabled === null) {
            res.status(400).json({ type: 'Error', code: 4, codeText: 'ET_INVALID_DATA',
              message: 'Instant Replay enable requires a boolean status field' });
            return;
          }
          const changed = state.irEnabled !== enabled;
          state.irEnabled = enabled;
          if (changed) emitCapture('/ShadowPlay/v.1.0/InstantReplay/Enable', { status: enabled });
          res.status(200).json({ status: enabled });
        });
        return;
      }
      if (p === '/ShadowPlay/v.1.0/Broadcast/Enable') {
        readBody(req, function (body) {
          const enabled = requiredBoolean(body, 'status');
          if (enabled === null) {
            res.status(400).json({ type: 'Error', code: 4, codeText: 'ET_INVALID_DATA',
              message: 'Broadcast enable requires a boolean status field' });
            return;
          }
          state.broadcastEnabled = enabled;
          res.status(200).json({ status: enabled });
        });
        return;
      }
      if (p === '/ShadowPlay/v.1.0/Microphone') {
        readBody(req, function (body) {
          const mode = body && typeof body.mode === 'string' ? body.mode.toLowerCase() : '';
          if (['ptt', 'alwayson', 'off'].indexOf(mode) < 0) {
            res.status(400).json({ type: 'Error', code: 4, codeText: 'ET_INVALID_DATA',
              message: 'Microphone mode must be PTT, AlwaysOn or Off' });
            return;
          }
          const s = loadSettings();
          s.microphone = Object.assign(s.microphone || {}, { mode: mode });
          if (!persistSettings(s, res)) return;
          res.status(200).json({});
        });
        return;
      }
      if (p === '/ShadowPlay/v.1.0/Microphone/PTT') {
        readBody(req, function (body) {
          if (!body || ['on', 'off'].indexOf(body.mode) < 0) {
            res.status(400).json({ type: 'Error', code: 4, codeText: 'ET_INVALID_DATA',
              message: 'Microphone PTT mode must be on or off' });
            return;
          }
          state.ptt = body.mode === 'on';
          res.status(200).json({});
        });
        return;
      }
      if (p === '/ShadowPlay/v.1.0/Webcam/Enable') {
        readBody(req, function (body) {
          const enabled = requiredBoolean(body, 'status');
          if (enabled === null) {
            res.status(400).json({ type: 'Error', code: 4, codeText: 'ET_INVALID_DATA',
              message: 'Webcam enable requires a boolean status field' });
            return;
          }
          state.webcamEnable = enabled;
          res.status(200).json({ enabled: enabled });
        });
        return;
      }
      if (p === '/ShadowPlay/v.1.0/CoPlay/Enable') {
        readBody(req, function (body) {
          const enabled = requiredBoolean(body, 'enable');
          if (enabled === null) {
            res.status(400).json({ type: 'Error', code: 4, codeText: 'ET_INVALID_DATA',
              message: 'CoPlay enable requires a boolean enable field' });
            return;
          }
          state.coplay = enabled;
          res.status(200).json({ enable: enabled });
        });
        return;
      }
      if (p === '/ShadowPlay/v.1.0/DesktopCapture/Enable') {
        readBody(req, function (body) {
          const enabled = requiredBoolean(body, 'enabled');
          if (enabled === null) {
            res.status(400).json({ type: 'Error', code: 4, codeText: 'ET_INVALID_DATA',
              message: 'Desktop Capture enable requires a boolean enabled field' });
            return;
          }
          const s = loadSettings();
          s.desktopCapture = enabled;
          if (!persistSettings(s, res)) return;
          state.desktopCapture = enabled;
          res.status(200).json({ enabled: enabled });
        });
        return;
      }
      if (p === '/ShadowPlay/v.1.0/Audio' || p === '/ShadowPlay/v.1.0/AudioSettings') {
        // POST เปลี่ยนค่าเสียง → เก็บจริง (ไฟล์ settings) — อ่าน body เอง (express ไม่ parse ให้)
        const key = (p === '/ShadowPlay/v.1.0/Audio') ? 'audio' : 'audioSettings';
        readBody(req, function (body) {
          if (!body || typeof body !== 'object' || Array.isArray(body)) {
            res.status(400).json({ type: 'Error', code: 4, codeText: 'ET_INVALID_DATA',
              message: 'Audio settings require a JSON object' });
            return;
          }
          const s = loadSettings();
          s[key] = Object.assign(s[key] || {}, body);
          if (!persistSettings(s, res)) return;
          res.status(200).json({});
        });
        return;
      }
      if (/^\/ShadowPlay\/v\.1\.0\/Microphone\/\d+\/Settings$/.test(p)) {
        readBody(req, function (body) {
          if (!body || typeof body !== 'object' || Array.isArray(body)) {
            res.status(400).json({ type: 'Error', code: 4, codeText: 'ET_INVALID_DATA',
              message: 'Microphone settings require a JSON object' });
            return;
          }
          const index = p.match(/\/Microphone\/(\d+)\/Settings$/)[1];
          const s = loadSettings();
          const current = s.microphoneSettings[index] || { index: Number(index), name: 'Default microphone', id: '' };
          s.microphoneSettings[index] = Object.assign({}, current, body, { index: Number(index) });
          if (!persistSettings(s, res)) return;
          res.status(200).json(s.microphoneSettings[index]);
        });
        return;
      }
      if (p === '/ShadowPlay/v.1.0/CustomOverlay/Enable' || /^\/ShadowPlay\/v\.1\.1\/CustomOverlay\/Enable\/\d+$/.test(p)) {
        readBody(req, function (body) {
          const enabled = requiredBoolean(body, 'enable');
          if (enabled === null) {
            res.status(400).json({ type: 'Error', code: 4, codeText: 'ET_INVALID_DATA',
              message: 'Custom overlay enable requires a boolean enable field' });
            return;
          }
          const s = loadSettings();
          const match = p.match(/\/Enable\/(\d+)$/);
          if (match) {
            const slot = s.customoverlay.slots[match[1]] || {};
            s.customoverlay.slots[match[1]] = Object.assign({}, slot, { enable: enabled });
          } else {
            s.customoverlay.enable = enabled;
          }
          if (!persistSettings(s, res)) return;
          res.status(200).json({ enable: enabled });
        });
        return;
      }
      if (p === '/ShadowPlay/v.1.0/CustomOverlay/Path' || /^\/ShadowPlay\/v\.1\.1\/CustomOverlay\/Path\/\d+$/.test(p)) {
        readBody(req, function (body) {
          if (!body || typeof body.path !== 'string') {
            res.status(400).json({ type: 'Error', code: 4, codeText: 'ET_INVALID_DATA',
              message: 'Custom overlay path requires a string path field' });
            return;
          }
          const s = loadSettings();
          const match = p.match(/\/Path\/(\d+)$/);
          if (match) {
            const slot = s.customoverlay.slots[match[1]] || {};
            s.customoverlay.slots[match[1]] = Object.assign({}, slot, { path: body.path });
          } else {
            s.customoverlay.path = body.path;
          }
          if (!persistSettings(s, res)) return;
          res.status(200).json({ path: body.path });
        });
        return;
      }
      if (p === '/ShadowPlay/v.1.0/Broadcast/Provider') {
        readBody(req, function (body) {
          if (!body || typeof body.provider !== 'string' || !body.provider) {
            res.status(400).json({ type: 'Error', code: 4, codeText: 'ET_INVALID_DATA',
              message: 'Broadcast provider requires a non-empty provider field' });
            return;
          }
          const s = loadSettings();
          s.broadcastPreference = Object.assign(s.broadcastPreference || {}, { provider: body.provider });
          if (!persistSettings(s, res)) return;
          res.status(200).json({ provider: body.provider });
        });
        return;
      }
      if (p === '/ShadowPlay/v.1.0/Highlights/Customize') {
        readBody(req, function (body) {
          if (!body || typeof body !== 'object' || Array.isArray(body)) {
            res.status(400).json({ type: 'Error', code: 4, codeText: 'ET_INVALID_DATA',
              message: 'Highlights settings require a JSON object' });
            return;
          }
          const s = loadSettings();
          if (Object.prototype.hasOwnProperty.call(body, 'sizeMB') &&
              (!Number.isFinite(body.sizeMB) || body.sizeMB < 0)) {
            res.status(400).json({ type: 'Error', code: 4, codeText: 'ET_INVALID_DATA',
              message: 'Highlights sizeMB must be a non-negative number' });
            return;
          }
          if (Object.prototype.hasOwnProperty.call(body, 'tempSaveFolder') &&
              typeof body.tempSaveFolder !== 'string') {
            res.status(400).json({ type: 'Error', code: 4, codeText: 'ET_INVALID_DATA',
              message: 'Highlights tempSaveFolder must be a string' });
            return;
          }
          s.highlights = Object.assign(s.highlights || {}, body);
          if (!persistSettings(s, res)) return;
          res.status(200).json({});
        });
        return;
      }
      const indicatorPost = p.match(/^\/ShadowPlay\/v\.1\.0\/Indicator\/([^/]+)\/Settings$/);
      if (indicatorPost) {
        readBody(req, function (body) {
          if (!body || typeof body !== 'object' || Array.isArray(body)) {
            res.status(400).json({ type: 'Error', code: 4, codeText: 'ET_INVALID_DATA',
              message: 'Indicator settings require a JSON object' });
            return;
          }
          const s = loadSettings();
          const id = indicatorPost[1].toLowerCase();
          s.indicators[id] = Object.assign(s.indicators[id] || {}, body);
          if (!persistSettings(s, res)) return;
          res.status(200).json(s.indicators[id]);
        });
        return;
      }
      if (p === '/SDK/v.1.0/Highlights/Enable') {
        readBody(req, function (body) {
          const enabled = requiredBoolean(body, 'enabled');
          if (enabled === null) {
            res.status(400).json({ type: 'Error', code: 4, codeText: 'ET_INVALID_DATA',
              message: 'Highlights enable requires a boolean enabled field' });
            return;
          }
          const s = loadSettings();
          s.highlights.enabled = enabled;
          if (!persistSettings(s, res)) return;
          res.status(200).json({ enabled: enabled });
        });
        return;
      }
      if (p === '/ShadowPlay/v.1.0/Record/Settings' || p === '/ShadowPlay/v.1.0/InstantReplay/Settings' ||
          p === '/ShadowPlay/v.1.0/Broadcast/Settings' || p === '/ShadowPlay/v.1.0/Webcam/Settings' ||
          p === '/ShadowPlay/v.1.0/RecordPaths') {
        // settings ทุกตัว: รวม body เข้าไฟล์ — ค่าคงอยู่ข้ามรีสตาร์ท
        const key = p.indexOf('Record/Settings') >= 0 ? 'record'
          : p.indexOf('InstantReplay') >= 0 ? 'instantReplay'
          : p.indexOf('Broadcast') >= 0 ? 'broadcast'
          : p.indexOf('Webcam') >= 0 ? 'webcam' : 'recordpaths';
        readBody(req, function (body) {
          if (!body || typeof body !== 'object' || Array.isArray(body)) {
            res.status(400).json({ type: 'Error', code: 4, codeText: 'ET_INVALID_DATA',
              message: 'OSC settings require a JSON object' });
            return;
          }
          const s = loadSettings();
          s[key] = Object.assign(s[key] || {}, body);
          if (!persistSettings(s, res)) return;
          res.status(200).json({});
        });
        return;
      }
      if (p.indexOf('/ShadowPlay/v.1.0/Hotkey/') === 0 && p.indexOf('/DynamicToggle') < 0) {
        // POST /Hotkey/<name> = ตั้งคีย์ใหม่ (เก็บลงไฟล์ — ค้างข้ามรีสตาร์ท)
        // POST /Hotkey/Monitor = เปิด/ปิดโหมดจับคีย์
        const name = p.substring('/ShadowPlay/v.1.0/Hotkey/'.length).toLowerCase();
        readBody(req, function (body) {
          if (name === 'monitor') {
            const enable = requiredBoolean(body, 'enable');
            if (enable === null) {
              res.status(400).json({ type: 'Error', code: 4, codeText: 'ET_INVALID_DATA',
                message: 'Hotkey monitor requires a boolean enable field' });
              return;
            }
            state.hotkeyMonitor = enable;
            res.status(200).json({});
            return;
          }
          // ★ validator แบบของแท้ (native แท้เป็นคนปฏิเสธ — เราทำแทน):
          //   ต้องมี modifier (Ctrl/Alt/Shift/Win) + มีปุ่มจริง + ไม่ซ้ำกับ hotkey อื่น
          const keys = body && Array.isArray(body.keys) ? body.keys : null;
          const hasMod = keys && keys.some(function (k) {
            return k === 16 || k === 17 || k === 18 || k === 91 || k === 92;
          });
          const vk = keys ? keys.filter(function (k) {
            return [16, 17, 18, 91, 92].indexOf(k) < 0;
          })[0] : undefined;
          if (!keys || !hasMod || vk === undefined || keys.length < 2) {
            // หน้า: e.data.code === ET_INVALID_DATA(4) → โชว์ข้อความ error ในหน้า
            res.status(400).json({ type: 'Error', code: 4, codeText: 'ET_INVALID_DATA',
              message: 'Hotkey must include a modifier (Ctrl/Alt/Shift/Win) plus a key' });
            return;
          }
          const s = loadSettings();
          // กันซ้ำ: คีย์ชุดเดียวกันถูกใช้โดยชื่ออื่นอยู่แล้ว?
          const cur = JSON.stringify(keys.slice().sort(function (a, b) { return a - b; }));
          const others = s.hotkeys || {};
          for (var on in others) {
            if (on === name) continue;
            const oc = JSON.stringify(others[on].slice().sort(function (a, b) { return a - b; }));
            if (oc === cur) {
              res.status(400).json({ type: 'Error', code: 4, codeText: 'ET_INVALID_DATA',
                message: 'Shortcut already in use by ' + on });
              return;
            }
          }
          s.hotkeys = s.hotkeys || {};
          s.hotkeys[name] = body.keys;
          if (!persistSettings(s, res)) return;
          res.status(200).json({});
        });
        return;
      }
    }

    // ---- GET floors ----
    if (m === 'GET') {
      const G = function (body) { res.status(200).json(body); };

      if (p === '/Notifier/v.1.0/settings') {
        return G(loadSettings().notifier || SETTINGS_DEFAULTS.notifier);
      }
      if (p === '/DulukaCapture/v.1.0/settings') {
        try {
          const paths = captureConfigPaths();
          if (!paths) return res.status(503).json({ type: 'Error', code: -1,
            codeText: 'CAPTURE_CONFIG_UNAVAILABLE',
            message: 'Unable to locate the shared NVIDIA ShadowPlay configuration directory' });
          return G(captureOptions(paths));
        } catch (e) {
          console.error('[osc-backend] capture options read failed: ' + e.message);
          return res.status(500).json({ type: 'Error', code: -1, codeText: 'CAPTURE_SETTINGS_READ_FAILED',
            message: 'Unable to read capture engine settings' });
        }
      }
      if (p === '/ShadowPlay/v.1.0/Capture/State') {
        if (state.recordRunning) return G({ captureMode: 0, recordingState: 1 });
        if (state.irRunning) return G({ captureMode: 1, recordingState: 1 });
        if (state.broadcastRunning) return G({ captureMode: 2, recordingState: 1 });
        return G({ captureMode: 0, recordingState: 0 });
      }
      if (p === '/ShadowPlay/v.1.0/Capture/PIDMode') return G({ valid: true });
      if (p.indexOf('/ShadowPlay/v.1.0/Capture/ProcessInfo') === 0) return G({});
      if (p === '/ShadowPlay/v.1.0/DesktopCapture/Enable') return G({ enabled: !!loadSettings().desktopCapture });
      if (p === '/ShadowPlay/v.1.0/DesktopCapture/Support') return G({ support: true });
      if (p.indexOf('/ShadowPlay/v.1.0/DesktopCapture/Support/Reason') === 0) return G({ support: true, unsupportReason: '' });

      if (p === '/ShadowPlay/v.1.0/Record/Enable') return G({ status: state.recordEnabled });
      if (p === '/ShadowPlay/v.1.0/Record/Running') return G({ running: state.recordRunning });
      if (p === '/ShadowPlay/v.1.0/Record/Settings') return G(settingsShape('record'));
      if (p === '/ShadowPlay/v.1.0/Record/Concurrency/Broadcast') return G({ supported: false });
      if (p === '/ShadowPlay/v.1.0/Record/Concurrency/Gamestream') return G({ supported: false });
      if (p === '/ShadowPlay/v.1.0/RecordPaths') return G(loadSettings().recordpaths);

      if (p === '/ShadowPlay/v.1.0/InstantReplay/Enable') return G({ status: state.irEnabled });
      if (p === '/ShadowPlay/v.1.0/InstantReplay/Running') return G({ running: state.irRunning });
      if (p === '/ShadowPlay/v.1.0/InstantReplay/Settings') return G(settingsShape('instantreplay'));
      if (p === '/ShadowPlay/v.1.0/InstantReplay/BufferLength') return G({ bufferLengthSeconds: 15 });

      if (p === '/ShadowPlay/v.1.0/Broadcast/Enable') return G({ status: state.broadcastEnabled });
      if (p === '/ShadowPlay/v.1.0/Broadcast/Running') return G({ running: state.broadcastRunning });
      if (p === '/ShadowPlay/v.1.0/Broadcast/Settings') return G(settingsShape('broadcast'));
      if (p === '/ShadowPlay/v.1.0/Broadcast/2KSupport') return G({ support: false });
      if (p === '/ShadowPlay/v.1.0/Broadcast/Provider') return G(loadSettings().broadcastPreference);
      if (p === '/ShadowPlay/v.1.0/Broadcast/Status') return G({ status: 'Stopped' });

      if (p === '/ShadowPlay/v.1.0/CustomOverlay/Support') return G({ support: 'multiple' });
      if (p === '/ShadowPlay/v.1.0/CustomOverlay/Enable') return G({ enable: !!loadSettings().customoverlay.enable });
      if (p === '/ShadowPlay/v.1.0/CustomOverlay/Path') return G({ path: loadSettings().customoverlay.path || '' });
      if (p === '/ShadowPlay/v.1.0/CustomOverlay/DefaultPath') return G({ defaultPath: loadSettings().recordpaths.videos || '' });
      if (p === '/ShadowPlay/v.1.0/CustomOverlay/Display') return G({ display: false });
      const overlaySlotEnable = p.match(/^\/ShadowPlay\/v\.1\.1\/CustomOverlay\/Enable\/(\d+)$/);
      if (overlaySlotEnable) {
        const slot = loadSettings().customoverlay.slots[overlaySlotEnable[1]] || {};
        return G({ enable: !!slot.enable });
      }
      const overlaySlotPath = p.match(/^\/ShadowPlay\/v\.1\.1\/CustomOverlay\/Path\/(\d+)$/);
      if (overlaySlotPath) {
        const slot = loadSettings().customoverlay.slots[overlaySlotPath[1]] || {};
        return G({ path: slot.path || '' });
      }
      if (p === '/ShadowPlay/v.1.0/Hotkey/Monitor') return G({ enable: !!state.hotkeyMonitor });

      if (p === '/ShadowPlay/v.1.0/Hotkey/openshare') return G({ keys: savedHotkey('openshare') });
      if (p.indexOf('/ShadowPlay/v.1.0/Hotkey/') === 0) {
        var hk = p.substring('/ShadowPlay/v.1.0/Hotkey/'.length).toLowerCase();
        return G({ keys: savedHotkey(hk) });
      }

      if (p === '/ShadowPlay/v.1.0/Audio') return G(loadSettings().audio);
      if (p === '/ShadowPlay/v.1.0/AudioSettings') return G(loadSettings().audioSettings);
      if (p === '/ShadowPlay/v.1.0/Microphone') return G(loadSettings().microphone);
      if (p === '/ShadowPlay/v.1.0/Microphone/Present') return G({ present: state.micPresent ? 1 : 0 });
      if (p === '/ShadowPlay/v.1.0/Microphone/Settings') return G(loadSettings().microphoneSettings['0']);
      const microphoneSettings = p.match(/^\/ShadowPlay\/v\.1\.0\/Microphone\/(\d+)\/Settings$/);
      if (microphoneSettings) {
        const saved = loadSettings().microphoneSettings[microphoneSettings[1]];
        return G(saved || { index: Number(microphoneSettings[1]), name: 'Default microphone', id: '', muted: false, volumePercent: 100, boostPercent: 0 });
      }
      if (p === '/ShadowPlay/v.1.0/Microphone/PTT') return G({ ptt: state.ptt });
      if (p === '/ShadowPlay/v.1.0/Webcam/Present') return G({ present: state.webcamPresent });
      if (p === '/ShadowPlay/v.1.0/Webcam/Settings') return G(loadSettings().webcam);
      if (p === '/ShadowPlay/v.1.0/Webcam/Enable') return G({ enabled: state.webcamEnable });
      if (p === '/ShadowPlay/v.1.0/Webcam/Shown') return G({ shown: state.webcamEnable });
      if (p === '/ShadowPlay/v.1.0/CoPlay/Enable') return G({ enable: state.coplay });

      const indicatorSettings = p.match(/^\/ShadowPlay\/v\.1\.0\/Indicator\/([^/]+)\/Settings$/);
      if (indicatorSettings) {
        const id = indicatorSettings[1].toLowerCase();
        const saved = loadSettings().indicators[id];
        if (saved) return G(saved);
      }
      if (p === '/ShadowPlay/v.1.0/Indicator/fps/Settings') return G({ state: state.fpsIndicator, position: 'top-right' });
      if (p === '/ShadowPlay/v.1.0/Indicator/fps/Support') return G({ support: true });
      if (p === '/ShadowPlay/v.1.0/Indicator/record/Settings') return G({ state: state.recordIndicator });
      if (p === '/ShadowPlay/v.1.0/Indicator/record/Support') return G({ support: true });
      if (p === '/ShadowPlay/v.1.0/Indicator/viewer/Support') return G({ support: true });

      if (p === '/ShadowPlay/v.1.0/GetHDRState') return G({ hdrState: false });
      if (p === '/ShadowPlay/v.1.0/Highlights/Customize') return G(loadSettings().highlights);
      if (p === '/ShadowPlay/v.1.0/Highlights/Session') return G({ active: false });
      if (p === '/ShadowPlay/v.1.0/CoPlay/Support') return G({ support: false });
      if (p === '/SDK/v.1.0/Highlights/Active') return G({ active: false });
      if (p === '/SDK/v.1.0/Highlights/Enable') return G({ enabled: !!loadSettings().highlights.enabled });
      if (p === '/SDK/v.1.0/Highlights/GetGamesConfig') return G({ games: [] });

      // ---- /Duluka plane: CAPTURE-REST-PLAN wire-through (engine nvsphelper64 polls these) ----
      if (p === '/Duluka/v.1.0/State') {
        // recordState: page Enable toggle → Starting → engine confirms Actual → Recording
        const rs = !state.recordEnabled ? 'Idle' : (state.recordRunning ? 'Recording' : 'Starting');
        const ir = !state.irEnabled ? 'Idle'
          : state.irSaving ? 'Saving' : state.irRunning ? 'Armed' : 'Starting';
        return G({
          recordState: rs,
          irState: ir,
          savePath: loadSettings().recordpaths.videos || '',
        });
      }
      if (p === '/Backend/v.1.0/health') return G({ status: 'ok' });

      if (p === '/ShadowPlay/v.1.0/8k60') return G({ supported: false });
      if (p === '/ShadowPlay/v.1.0/Screenshot/Support') return G({ support: true });
      if (p === '/QuietMode2/v.1.0/support') return G({ supported: false });
      if (p === '/QuietMode2/v.1.0/state') return G({ supported: false, enabled: false, baseFrameRate: 0, fanVolume: 0 });
      if (/^\/Nis2\/v\.1\.0\/[^/]+\/state$/.test(p)) {
        return G({ cmsId: p.split('/')[3], supported: false, enabled: false, sharpen: 0, selectedResolutionIndex: 0 });
      }
      if (/^\/DeepDVC\/v\.1\.0\/[^/]+\/state$/.test(p)) {
        return G({ cmsId: p.split('/')[3], supported: false, enabled: false, vibrance: 0, saveToDRS: false });
      }
      if (p === '/NvCamera/v.1.0/Compatible') return G({ compatible: true });
      if (p === '/ShadowPlay/v.1.0/Launch') return G({ launch: true });
      if (p === '/beta') return G({ beta: false });
      if (p === '/SDK/v.1.0/Highlights/Active') return G({ active: false });
    }

    next();
  });
};
