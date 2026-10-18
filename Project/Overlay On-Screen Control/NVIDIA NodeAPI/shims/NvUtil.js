// nv-util.js — JS shim for NvUtil.node (genuine NvNode, node v11 ABI).
// Standalone posture: no NVIDIA services, no single-instance lock, port
// pinned to 59001, security cookie check disabled (the page is paired by
// our own host via QUERY_WIN_NODE_INFO).
'use strict';

const crypto = require('crypto');
const fs = require('fs');
const path = require('path');

const PORT = 59011;
const VERSION = '3.28.0.412';

// Delegate kernel-object duties to the genuine NvUtil.node when loadable:
// the launcher's "Node already running" check and Share's node-info read are
// based on the real named event/mapping (Global\{1E6C4F0F-...}), which pure
// JS cannot create. Everything else stays shimmed.
let native = null;
try { native = require('../NvUtil.node'); } catch (e) { native = null; }

function dataDir() {
  // Keep runtime state inside our product tree.
  return path.join(__dirname, '..', '..', '..', 'NVIDIA Config', 'nvnode');
}

module.exports = {
  ClaimSingleInstance: function () {
    // ★ coexistence: NVIDIA App แท้จด single-instance event ของตัวเองไว้แล้ว —
    //   เรียก native (เห็น event ของแท้) จะทำให้ node เราคิดว่ามีอยู่แล้ว/ชนกัน
    //   ของเราไม่มี lock ก็รันคู่กันได้
    return true;
  },

  GenerateRandom: function (n) {
    try { return crypto.randomBytes(n || 16).toString('hex'); }
    catch (e) { return '0000000000000000'; }
  },

  GetPortOverride: function () { return PORT; },

  GetGFEVersionSync: function () { return VERSION; },


  GetGFEArchSync: function () { return ''; },
  DeleteGFE2BetaFlagSync: function () { return true; },
  GetGpuArch: function () { return ''; },
  GetGFE3BetaFlagSync: function () { return false; },

  GetLanguage: function () { return 'en-US'; },

  SaveLanguage: function (lang) { try { this._lang = lang; } catch (e) {} },

  GetLocalAppdataPath: function () {
    try { fs.mkdirSync(dataDir(), { recursive: true }); } catch (e) {}
    return dataDir();
  },

  GetProgramDataPath: function () {
    const d = dataDir();
    try { fs.mkdirSync(d, { recursive: true }); } catch (e) {}
    return d;
  },

  GetSystemServiceStatus: function (name) { return true; },

  StartSystemService: function (name) { return true; },

  WaitSystemService: function (name) { return true; },

  SetExitCallback: function (cb) { this._exitCb = cb; },

  VerifyFileSignatureSync: function (file) {
    // Standalone: our own shims/signed-genuine addons pass unconditionally.
    return true;
  },

  IsSecurityCheckEnabled: function () { return false; },




  DestroyLogger: function () { return true; },
  LogInfoSync: function (line) { try { console.log('[nvnode] ' + line); } catch (e) {} },
  LogErrorSync: function (line) { try { console.error('[nvnode] ' + line); } catch (e) {} },
  LogDebugSync: function (line) { try { console.log('[nvnode:debug] ' + line); } catch (e) {} },
  LogWarnSync: function (line) { try { console.warn('[nvnode:warn] ' + line); } catch (e) {} },
  CreateLogger: function (logPath) { return true; },
  LogError: function (line) { try { console.error('[nvnode] ' + line); } catch (e) {} },
  LogDebug: function (line) { try { console.log('[nvnode:debug] ' + line); } catch (e) {} },
  LogInfo: function (line) { try { console.log('[nvnode] ' + line); } catch (e) {} },
  LogWarn: function (line) { try { console.warn('[nvnode:warn] ' + line); } catch (e) {} },
  ConfirmInitialization: function (json) {
    // ★ coexistence: native เขียน MMF/init ชื่อเดียวกับ NVIDIA App แท้ — ห้ามเรียก
    //   เขียนไฟล์ -duluka ของเราเอง (ผู้อ่าน: NvCapture/แม่ ชี้มาที่ไฟล์นี้)
    try {
      fs.writeFileSync(path.join(dataDir(), 'nvnode-init-duluka.json'),
                       String(json), 'utf8');
    } catch (e) {}
    return true;
  },
};
