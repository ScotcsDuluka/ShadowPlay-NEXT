#!/usr/bin/env node
'use strict'
// index.js — NVIDIA ShadowPlay Portable backend.
//
// Clean-room replacement for NVIDIA Web Helper.exe (NvNode). One process:
//   - REST + static on http://127.0.0.1:59011 (the managed Node API port,
//     shared with NvContainer and the native OSC host)
//   - serves the osc frontend (../osc in the product tree) same-origin,
//     so the overlay loads through this server and socket.io stays
//     same-origin too
//   - /ShadowPlay + /SDK + /FramerateLimiter  → routes/shadowplay.js
//   - /ShadowPlay/.../GetCustomize + sliders  → routes/customize.js
//   - /HardwareInformation + /SystemInfo      → routes/hardware.js
//   - /Account + /Duluka + /DulukaCapture + /PiplConfig → routes/duluka.js
//   - /Debug/PageLog + /beta + health         → routes/debug.js
//   - socket.io v2 push channel               → socket.js
//
// Boot contract (standalone mode, all proven on the Intel floor):
//   - never exits on stray errors (uncaughtException → log + keep serving)
//   - never blocks boot on hardware probing (generic floor swaps in async)
//   - storage failures degrade to no-persistence, never to a crash

const http = require('http');
const fs = require('fs');
const path = require('path');

const pkg = require('./package.json');
const config = require('./config');
const loggerLib = require('./lib/logger');
const storeLib = require('./lib/store');
const defaults = require('./lib/defaults');
const hardwareProbe = require('./lib/hardwareProbe');
const socketLib = require('./socket');

// ── boot ────────────────────────────────────────────────────────────
const cfg = config.loadConfig();
const logger = loggerLib.createLogger(cfg.logDir, cfg.logLevel);
const store = storeLib.createStore(cfg.dataDir);

logger.info('ShadowPlay Portable backend ' + pkg.version + ' starting (node ' + process.version + ')');
logger.info('config: port=' + cfg.port + ' osc=' + cfg.oscDir + ' dulukaServer=' + cfg.dulukaServer +
    ' securityCheck=' + cfg.securityCheck);

// standalone mode: never tear the process down for a stray background
// failure — the overlay keeps serving (the Web Helper's own lesson, see
// the rescued index.js OnUnhandledError)
process.on('uncaughtException', function (err) {
    logger.error('uncaughtException: ' + (err && err.stack ? err.stack : err));
    logger.error('STANDALONE: keeping process alive after error');
});
process.on('unhandledRejection', function (err) {
    logger.error('unhandledRejection: ' + (err && err.stack ? err.stack : err));
});
process.on('SIGINT', function () { shutdown('SIGINT'); });
process.on('SIGTERM', function () { shutdown('SIGTERM'); });

// ── express app ─────────────────────────────────────────────────────
const app = require('express')();
app.disable('x-powered-by');

// CORS — every response, exactly the Web Helper's header set
app.use(function (req, res, next) {
    res.header('Access-Control-Allow-Origin', '*');
    res.header('Access-Control-Allow-Methods', 'GET,POST');
    res.header('Access-Control-Allow-Headers', 'X_LOCAL_SECURITY_COOKIE, Content-Type, Content-Length');
    if (req.method === 'OPTIONS') {
        res.writeHead(204);
        res.end();
        return;
    }
    next();
});

// request log with id (mirrors the Web Helper's debug middleware)
let nextRequestId = 1;
app.use(function (req, res, next) {
    const requestId = nextRequestId++;
    logger.debug('Incoming request  #' + requestId + ': ' + req.method + ' ' + req.originalUrl);
    res.on('finish', function () {
        logger.debug('Response finished #' + requestId + ': ' + req.method + ' ' +
            req.originalUrl + ' with status ' + res.statusCode);
    });
    next();
});

// body reader — the page and the hotkey listener POST JSON without always
// setting Content-Type, so read raw and parse leniently (ReadJsonBody
// semantics from the proven backend), capped at 5 MB
const BODY_LIMIT = 5 * 1024 * 1024;
app.use(function (req, res, next) {
    if (req.method !== 'POST' && req.method !== 'PUT') return next();
    let size = 0;
    const chunks = [];
    req.on('data', function (chunk) {
        size += chunk.length;
        if (size > BODY_LIMIT) { req.destroy(); return; }
        chunks.push(chunk);
    });
    req.on('end', function () {
        const raw = Buffer.concat(chunks).toString('utf8');
        req.rawBody = raw;
        if (raw && raw.length) {
            try { req.body = JSON.parse(raw); } catch (err) { req.body = raw; }
        } else {
            req.body = {};
        }
        next();
    });
    req.on('error', function () { next(); });
});

// static osc frontend — same-origin serving for the overlay window
let oscServed = false;
try {
    if (fs.existsSync(path.join(cfg.oscDir, 'index.html'))) {
        app.use(express_static(cfg.oscDir));
        oscServed = true;
        logger.info('osc frontend served from ' + cfg.oscDir);
    } else {
        logger.warn('osc frontend NOT found at ' + cfg.oscDir + ' — API-only mode ' +
            '(set OSC_DIR or deploy <GFE>\\osc next to Backend)');
    }
} catch (err) {
    logger.warn('osc static check failed: ' + err.message);
}
function express_static(root) {
    const opts = { index: 'index.html', maxAge: 0, dotfiles: 'ignore' };
    return require('express').static(root, opts);
}

// http server + socket.io v2 (routes need the socket API for emits)
const httpServer = http.createServer(app);
const socket = socketLib.create(httpServer, logger, cfg);

// hardware floor — generic shape synchronously, WMI probe swaps in async
const hardware = hardwareProbe.ensureHardwareFloor(cfg, logger, defaults);

// routes (ctx shared with every module)
const ctx = {
    cfg: cfg, logger: logger, store: store,
    socket: socket, defaults: defaults, hardware: hardware
};

require('./routes/debug')(app, ctx);        // PageLog first: page console flows in during boot
require('./routes/customize')(app, ctx);    // GetCustomize + slider bounds
require('./routes/shadowplay')(app, ctx);   // the big state store
require('./routes/hardware')(app, ctx);
require('./routes/duluka')(app, ctx);
require('./routes/families')(app, ctx);     // QuietMode2/Nis2/DeepDVC/Feedback/abHub/gfeupdate/GameShare
require('./routes/gallery')(app, ctx);      // /Gallery HTTP (GetFolderListing/EnumerateDrives/...)

// OUR LANE: cefQuery — หน้าส่ง {command} มาทาง POST /cefquery (cefquery-shim.js)
// QUERY_WIN_NODE_INFO = หน้าต้องใช้ก่อนวาดเมนู (route resolve localNodeInfo)
app.post('/cefquery', function (req, res) {
    var body = '';
    req.on('data', function (c) { body += c; });
    req.on('end', function () {
        var reply = '{}';
        try {
            var q = JSON.parse(body || '{}');
            if (q.command === 'QUERY_WIN_NODE_INFO') {
                reply = JSON.stringify({
                    port: cfg.port, disableSecurity: true, securityCookie: '',
                    gfwsl: { server: 'https://gfwsl.geforce.com/' },
                    jarvis: cfg.jarvis, gxtarget: cfg.gxtarget
                });
            }
        } catch (e) {}
        res.header('Content-Type', 'application/json');
        res.header('Access-Control-Allow-Origin', '*');
        res.end(reply);
    });
});

// 404 — mirror the REAL backend's exact miss shape
// ("Cannot POST /path\n" text — floor captures, commit 01b6a02)
app.use(function (req, res) {
    res.status(404).type('text/plain').send('Cannot ' + req.method + ' ' + req.originalUrl + '\n');
});

// error handler
app.use(function (err, req, res, next) { // eslint-disable-line no-unused-vars
    logger.error('request error ' + req.method + ' ' + req.originalUrl + ': ' + err);
    try {
        res.status(500).json({ type: 'Error', code: -1, codeText: 'Unknown/Internal error', message: String(err && err.message || err) });
    } catch (e) { /* headers already sent */ }
});

// ── listen ──────────────────────────────────────────────────────────
httpServer.on('error', function (err) {
    logger.error('server error: ' + (err && err.stack ? err.stack : err));
    if (err && err.code === 'EADDRINUSE') {
        logger.error('port ' + cfg.port + ' already in use — is another backend (or the real Web Helper) running?');
        process.exit(1);
    }
});

httpServer.listen(cfg.port, cfg.host, function () {
    const routeCount = countRoutes(app);
    logger.info('Backend listening on http://' + cfg.host + ':' + cfg.port +
        ' (' + routeCount + ' routes, osc ' + (oscServed ? 'served' : 'API-only') + ', no cookie)');
    logger.info('Alt+Z wire-through ready: POST /ShadowPlay/v.1.0/Hotkey/Toggle');
});

let shuttingDown = false;
function shutdown(signal) {
    if (shuttingDown) return;
    shuttingDown = true;
    logger.info('shutdown (' + signal + ') — flushing store');
    try { store.flush(); } catch (e) { /* keep going */ }
    try {
        httpServer.close(function () { process.exit(0); });
        setTimeout(function () { process.exit(0); }, 1500).unref();
    } catch (e) {
        process.exit(0);
    }
}

function countRoutes(app) {
    let n = 0;
    for (const layer of app._router.stack) {
        if (layer.route) n++;
        if (layer.name === 'router' && layer.handle && layer.handle.stack) n += layer.handle.stack.length;
    }
    return n;
}
