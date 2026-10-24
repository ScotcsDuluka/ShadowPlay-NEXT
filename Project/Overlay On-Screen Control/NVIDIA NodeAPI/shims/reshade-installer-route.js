'use strict';

// Read-only installer preflight. ReShade's official setup owns installation,
// updates and removal. Never copy or delete game DLLs from a local API request.
const fs = require('fs');
const path = require('path');
const OFFICIAL_URL = 'https://reshade.me/';
const PROXIES = Object.freeze(['dxgi.dll', 'd3d9.dll', 'd3d10.dll', 'd3d11.dll', 'opengl32.dll']);

function invalid(message) {
  const error = new Error(message);
  error.status = 400;
  error.codeText = 'INVALID_GAME_EXECUTABLE';
  return error;
}
function readJson(req) {
  if (req.body && typeof req.body === 'object' && !Array.isArray(req.body)) return Promise.resolve(req.body);
  return new Promise((resolve, reject) => {
    let raw = '';
    req.on('data', chunk => {
      raw += chunk;
      if (raw.length > 8192) {
        reject(invalid('Request body is too large'));
        req.destroy();
      }
    });
    req.on('end', () => {
      try {
        const data = JSON.parse(raw || '{}');
        if (!data || typeof data !== 'object' || Array.isArray(data)) throw new Error('Expected object');
        resolve(data);
      } catch (_) { reject(invalid('A JSON object is required')); }
    });
    req.on('error', reject);
  });
}
function inspect(executable, filesystem) {
  const io = filesystem || fs;
  if (typeof executable !== 'string' || executable.length > 1024 ||
      !path.win32.isAbsolute(executable) || !/\.exe$/i.test(executable) ||
      !/^[a-zA-Z]:[\\/]/.test(executable)) throw invalid('Choose an absolute local Windows game .exe path');
  const requested = path.win32.normalize(executable);
  let stat;
  try { stat = io.statSync(requested); }
  catch (_) { throw invalid('The game executable is not accessible'); }
  if (!stat.isFile()) throw invalid('The target must be a game executable file');
  const directory = path.win32.dirname(requested);
  const files = new Set(io.readdirSync(directory).map(name => name.toLowerCase()));
  const proxies = PROXIES.filter(name => files.has(name));
  const hasConfiguration = files.has('reshade.ini');
  const hasLog = files.has('reshade.log');
  const hasShaderDirectory = files.has('reshade-shaders');
  let hasReShadeLogEvidence = false;
  if (hasLog) {
    try {
      const file = path.win32.join(directory, 'ReShade.log');
      const handle = io.openSync(file, 'r');
      try {
        const size = io.fstatSync(handle).size;
        const limit = Math.min(size, 65536);
        const buffer = Buffer.alloc(limit);
        io.readSync(handle, buffer, 0, limit, Math.max(0, size - limit));
        hasReShadeLogEvidence = /Initializing crosire.s ReShade|Registered add-on .NVIDIA Ansel Adapter|ReShade version/i.test(buffer.toString('utf8'));
      } finally { io.closeSync(handle); }
    } catch (_) { /* A log is optional, never proof by itself. */ }
  }
  const evidence = {
    proxyDllNames: proxies,
    reshadeConfigPresent: hasConfiguration,
    reshadeLogPresent: hasLog,
    reshadeLogIdentified: hasReShadeLogEvidence,
    shadersFolderPresent: hasShaderDirectory
  };
  // A proxy named dxgi.dll can belong to an unrelated application or overlay.
  const detected = hasReShadeLogEvidence && (proxies.length > 0 || hasConfiguration);
  const status = detected ? 'detected' :
    (proxies.length || hasConfiguration || hasLog) ? 'possible' : 'not-detected';
  // These are graphics API *hints*, not proof of the API used by a game.
  // Never select an injection DLL from a filename alone.
  const hints = [];
  if (proxies.includes('d3d9.dll')) hints.push('d3d9');
  if (proxies.some(name => ['dxgi.dll','d3d10.dll','d3d11.dll'].includes(name)))
    hints.push('d3d10','d3d11','d3d12');
  if (proxies.includes('opengl32.dll')) hints.push('opengl');
  return {
    schemaVersion: 1, status, detected,
    installationStatus: detected ? 'Installed' :
      status === 'possible' ? 'Unknown' : 'Not Installed',
    installationScope: 'game-directory-only',
    executable: requested, directory,
    evidence, graphicsApiHints: Array.from(new Set(hints)),
    graphicsApiVerified: false,
    mutationSupported: false, gameRunning: 'not-verified',
    operation: 'official-setup-required',
    officialUrl: OFFICIAL_URL,
    message: detected ? 'ReShade evidence found. Verify it in the official setup before changing files.' :
      status === 'possible' ? 'Existing graphics proxy or configuration found; its owner is not verified. Do not overwrite it.' :
      'No ReShade evidence found in this executable directory. This does not rule out a global install.',
    warning: 'Close the game first. Check anti-cheat rules and any existing DLL ownership before proceeding.'
  };
}
// Windows tasklist only checks matching executable *name*; not path identity.
function observeRunning(executable) {
  return new Promise(resolve => {
    const name = path.win32.basename(executable);
    require('child_process').execFile('tasklist.exe',
      ['/FI', 'IMAGENAME eq ' + name, '/FO', 'CSV', '/NH'],
      {windowsHide:true, timeout:4000, maxBuffer:65536},
      (error, stdout) => {
        if (error || typeof stdout !== 'string') return resolve('not-verified');
        const found = stdout.split(/\r?\n/).some(line =>
          line.trim().toLowerCase().startsWith(('"' + name + '",').toLowerCase()));
        resolve(found ? 'possible' : 'not-seen');
      });
  });
}
function mount(app) {
  const base = '/Duluka/v.1.0/ReShade/Installer';
  app.get(base + '/Info', (req, res) => {
    res.set('Cache-Control', 'no-store').json({
      schemaVersion: 1, mode: 'guided', officialUrl: OFFICIAL_URL,
      nativeInstall: false, nativeUninstall: false, reason: 'Official ReShade setup performs game file changes.',
      note: 'Game Filter / ReShade Ansel engine remains independent from installer status.'
    });
  });
  app.post(base + '/Inspect', async (req, res) => {
    res.set('Cache-Control', 'no-store');
    try {
      const body = await readJson(req);
      const report = inspect(body.executable);
      report.gameRunning = await observeRunning(report.executable);
      report.evidence.processNameMatch = report.gameRunning === 'possible';
      res.json(report);
    } catch (error) {
      res.status(error.status || 500).json({codeText:error.codeText || 'RESHADER_INSPECT_FAILED',
        message:error.status ? error.message : 'Could not inspect the selected executable'});
    }
  });
}
module.exports = {mount,inspect,OFFICIAL_URL};