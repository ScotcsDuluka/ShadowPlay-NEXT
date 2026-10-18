// capture-manager.js — ตัวอัดของเราเอง (แทน legacy engine):
//   recordEnabled (ปุ่ม Record/Alt+F9) → spawn ffmpeg ddagrab + h264_nvenc
//   → กดปิด = ส่ง 'q' ให้ไฟล์ finalize → mp4 ใน Videos
//   watchdog 500ms: state.recordEnabled เป็นตัวตั้ง ตัวเดียว (route-floor คือ authority)
'use strict';

const CP = require('child_process');
const path = require('path');
const fs = require('fs');

const FFMPEG = path.join(__dirname, '..', 'ffmpeg', 'ffmpeg.exe');

const CAP = {
  proc: null,        // ffmpeg process ที่กำลังอัด
  outPath: null,
  startedAt: null,
  stopping: false,
};

function log(m) {
  try {
    const dir = path.join(__dirname, '..', 'Logs');
    fs.mkdirSync(dir, { recursive: true });
    fs.appendFileSync(path.join(dir, 'capture.log'),
      new Date().toISOString().replace('T', ' ').slice(0, 19) + ' ' + m + '\n');
  } catch (e) { }
}

function startCapture(state, loadSettings) {
  if (CAP.proc) return;
  try {
    const st = loadSettings();
    const videos = (st.recordpaths && st.recordpaths.videos) || process.env.USERPROFILE + '\\Videos';
    fs.mkdirSync(videos, { recursive: true });
    const fps = (st.record && st.record.framerate) || 60;
    const bitrate = Math.round(((st.record && st.record.bitrateBps) || 50000000) / 1000000);
    const ts = new Date().toISOString().replace(/[:T]/g, '-').slice(0, 19);
    const out = path.join(videos, 'ShadowPlay-' + ts + '.mp4');
    const res = (st.record && st.record.resolution);

    // ddagrab = desktop duplication เป็น D3D11 texture → h264_nvenc กิน texture ตรง ๆ (GPU ทั้งสาย)
    const args = ['-y', '-hide_banner', '-loglevel', 'warning', '-stats'];
    if (res === 'In-game' || !res) {
      args.push('-filter_complex', 'ddagrab=framerate=' + fps);
    } else {
      // จอสูงกว่าที่ขอ → scale หลัง grab (ddagrab คืน D3D11 → scale_cuda ก่อน nvenc)
      const map = { '2160p 4K': [3840, 2160], '1440p HD': [2560, 1440], '1080p HD': [1920, 1080], '720p HD': [1280, 720], '480p': [854, 480], '360p': [640, 360], '240p': [426, 240] };
      const wh = map[res];
      if (wh) args.push('-filter_complex', 'ddagrab=framerate=' + fps + ',scale_cuda=' + wh[0] + ':' + wh[1]);
      else args.push('-filter_complex', 'ddagrab=framerate=' + fps);
    }
    args.push('-c:v', 'h264_nvenc', '-preset', 'p4', '-rc', 'vbr',
      '-b:v', bitrate + 'M', '-maxrate', bitrate + 'M', '-bufsize', (bitrate * 2) + 'M',
      out);

    CAP.proc = CP.spawn(FFMPEG, args, { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
    CAP.outPath = out;
    CAP.startedAt = Date.now();
    log('start → ' + out + ' (fps ' + fps + ', ' + bitrate + 'M, res ' + (res || 'In-game') + ') pid=' + CAP.proc.pid);

    CAP.proc.stderr.on('data', function (d) {
      const s = d.toString();
      if (s.indexOf('frame=') >= 0 || s.indexOf('Error') >= 0 || s.indexOf('error') >= 0) {
        fs.appendFileSync(path.join(__dirname, '..', 'Logs', 'capture.log'), s.trim() + '\n');
      }
    });
    CAP.proc.on('exit', function (code) {
      const dur = CAP.startedAt ? Math.round((Date.now() - CAP.startedAt) / 1000) : 0;
      log('exit code=' + code + ' ระยะเวลา ' + dur + 's → ' + CAP.outPath);
      CAP.proc = null;
      CAP.stopping = false;
    });
  } catch (e) {
    log('start FAILED: ' + e.message);
    CAP.proc = null;
  }
}

function stopCapture(done) {
  if (!CAP.proc) { if (done) done(); return; }
  if (CAP.stopping) return;
  CAP.stopping = true;
  try { CAP.proc.stdin.write('q'); } catch (e) { try { CAP.proc.kill(); } catch (e2) { } }
  // กันค้าง: 10 วิไม่ตายจริง → kill ตรง
  const proc = CAP.proc;
  setTimeout(function () {
    try { if (proc && proc.exitCode === null) proc.kill(); } catch (e) { }
  }, 10000);
  if (done) done();
}

function installCaptureWatchdog(state, loadSettings) {
  setInterval(function () {
    try {
      if (state.recordEnabled && !CAP.proc) startCapture(state, loadSettings);
      if (!state.recordEnabled && CAP.proc) stopCapture();
      // sync running flag (หน้าอ่าน /Record/Running จากตรงนี้)
      state.recordRunning = !!CAP.proc;
    } catch (e) { log('watchdog err: ' + e.message); }
  }, 500);
}

module.exports = { installCaptureWatchdog, CAP };
