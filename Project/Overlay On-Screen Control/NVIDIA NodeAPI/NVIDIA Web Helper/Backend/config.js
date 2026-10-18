'use strict'
// config.js — runtime configuration resolution: env > config.json > defaults.
//
// Port 59011 is shared with NvContainer and the native OSC host.

const fs = require('fs');
const path = require('path');

const ROOT = __dirname;

const DEFAULTS = {
    // loopback only — the osc frontend and the engine run on this machine
    host: '127.0.0.1',
    port: 59011,
    // product tree: NvBackend serves the OSC bundle owned by NvOverlay\\CEF.
    // Override with OSC_DIR / config.json when a different bundle is required.
    oscDir: path.join(ROOT, '..', 'NvOverlay', 'CEF', 'Resources', 'osc'),
    // Duluka Server (account provider backend) — jarvis.server lands here
    dulukaServer: 'http://127.0.0.1:5115',
    // standalone mode: no X_LOCAL_SECURITY_COOKIE handshake (disableSecurity=1)
    securityCheck: false,
    // re-run the WMI probe even when data/hardware-floor.json exists
    reprobeHardware: false,
    logLevel: 'info'
};

function loadConfig() {
    const cfg = Object.assign({}, DEFAULTS);

    // config.json next to this file wins over defaults
    try {
        const fileCfg = JSON.parse(fs.readFileSync(path.join(ROOT, 'config.json'), 'utf8'));
        for (const k of Object.keys(DEFAULTS)) {
            if (fileCfg[k] !== undefined) cfg[k] = fileCfg[k];
        }
    } catch (err) { /* no config.json — defaults only */ }

    // environment wins over everything (deployment ergonomics)
    if (process.env.NVSP_HOST) cfg.host = process.env.NVSP_HOST;
    if (process.env.NVSP_PORT) cfg.port = parseInt(process.env.NVSP_PORT, 10) || cfg.port;
    if (process.env.OSC_DIR) cfg.oscDir = process.env.OSC_DIR;
    if (process.env.DULUKA_SERVER) cfg.dulukaServer = process.env.DULUKA_SERVER;
    if (process.env.NVSP_SECURITY_CHECK === '1') cfg.securityCheck = true;
    if (process.env.HARDWARE_REPROBE === '1') cfg.reprobeHardware = true;
    if (process.env.NVSP_LOG_LEVEL) cfg.logLevel = process.env.NVSP_LOG_LEVEL;
    if (process.env.NVSP_DATA_DIR) cfg.dataDir = path.resolve(process.env.NVSP_DATA_DIR);

    cfg.oscDir = path.resolve(cfg.oscDir);
    if (!cfg.dataDir) cfg.dataDir = path.join(ROOT, 'data');
    cfg.logDir = path.join(cfg.dataDir, 'logs');
    return cfg;
}

module.exports = { loadConfig, DEFAULTS, ROOT };
