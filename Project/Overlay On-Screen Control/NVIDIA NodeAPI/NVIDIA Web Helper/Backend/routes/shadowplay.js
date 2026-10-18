'use strict'
// routes/shadowplay.js — the /ShadowPlay/v.1.0/* state store + the /SDK and
// FramerateLimiter floors + the OSC lifecycle handshakes.
//
// GET  reads the section (defaults merged over), POST merges the body into
// data/state.json (store.js — atomic, survives restarts). Response shapes
// are the REAL backend captures (lib/floor-defaults.json — commit 01b6a02)
// with the DulukaFloor.js store semantics (proven on Intel 2026-09-19/22).
//
// Capture-state routes (Enable/Running) answer the standalone idle shape.
// Phase 3 wires the engine to this same API — the engine then flips these
// through lib/enginestate.js instead (same routes, live data).

const defaults = require('../lib/defaults');

module.exports = function shadowplayRoutes(app, ctx) {
    const store = ctx.store;
    const socket = ctx.socket;

    function sectionOr(name, fallback) {
        const sec = store.getSection(name);
        return Object.keys(sec).length > 0 ? sec : fallback;
    }

    // ── per-feature settings (GET read / POST persist) ──────────────
    settingsPair('/Record/Settings', 'recordSettings', defaults.SETTINGS_DEFAULTS.record);
    settingsPair('/InstantReplay/Settings', 'instantReplaySettings', defaults.SETTINGS_DEFAULTS.instantreplay);
    settingsPair('/Broadcast/Settings', 'broadcastSettings', defaults.SETTINGS_DEFAULTS.broadcast);

    function settingsPair(pathSuffix, section, def) {
        app.get('/ShadowPlay/v.1.0' + pathSuffix, function (req, res) {
            res.status(200).json(Object.assign({}, def, store.getSection(section)));
        });
        app.post('/ShadowPlay/v.1.0' + pathSuffix, function (req, res) {
            if (req.body && typeof req.body === 'object') {
                store.storeSection(section, req.body);
                ctx.logger.info('settings saved: ' + section);
            }
            res.status(200).json({});
        });
    }

    // ── record paths ────────────────────────────────────────────────
    app.get('/ShadowPlay/v.1.0/RecordPaths', function (req, res) {
        const def = {
            videos: path_videos(),
            tempFiles: tmpdir()
        };
        res.status(200).json(Object.assign(def, store.getSection('recordPaths')));
    });
    app.post('/ShadowPlay/v.1.0/RecordPaths', function (req, res) {
        if (req.body && typeof req.body === 'object') store.storeSection('recordPaths', req.body);
        res.status(200).json({});
    });

    // ── Instant Replay save ─────────────────────────────────────────
    // POST /InstantReplay/Save → the production bridge emits
    // /InstantReplay/Save {status:true} when the buffer is saved
    // (capture mode 1, recordingState 0x20 — NvShadowPlayAPI.js:2773).
    app.post('/ShadowPlay/v.1.0/InstantReplay/Save', function (req, res) {
        store.storeSection('instantReplaySave', { savedAt: new Date().toISOString() });
        socket.emitChannel('/ShadowPlay/v.1.0/InstantReplay/Save', { status: true });
        ctx.logger.info('instant replay saved');
        res.status(200).json({});
    });

    // ── capture enable/running (engine-driven state) ────────────────
    // The osc page AND the engine (Phase 3 REST client, replacing the
    // TCP :5001 channel) speak this exact surface:
    //   POST /Record/Enable {status:true|false}  → stored + socket push
    //   GET  /Record/Enable                      → {status}
    //   POST /Record/Running {running:bool}      → engine publishes live truth
    //   GET  /Record/Running                     → {running} (default false)
    const FEATURES = 'Record|InstantReplay';
    app.get(new RegExp('^/ShadowPlay/v\\.1\\.0/(' + FEATURES + ')/Enable/?$'), function (req, res) {
        const feature = req.params[0].toLowerCase();
        const sec = store.getSection(feature + 'Enable');
        res.status(200).json({ status: sec.status === true });
    });
    app.post(new RegExp('^/ShadowPlay/v\\.1\\.0/(' + FEATURES + ')/Enable/?$'), function (req, res) {
        const feature = req.params[0].toLowerCase();
        const body = (req.body && typeof req.body === 'object') ? req.body : {};
        const status = body.status === true;
        store.storeSection(feature + 'Enable', { status: status, changedAt: new Date().toISOString() });
        // production parity: the state flip emits the feature's own channel
        // with {status} — the same frame the native CaptureStateChange
        // notification produces for modes 0/1-0x44|0x45
        // (docs/OSC-NODE-API-EXTRACTION.md §A.1).
        socket.emitChannel('/ShadowPlay/v.1.0/' + req.params[0] + '/Enable', { status: status });
        ctx.logger.info('capture enable: ' + feature + ' → ' + status);
        res.status(200).json({ status: status });
    });
    app.get(new RegExp('^/ShadowPlay/v\\.1\\.0/(' + FEATURES + ')/Running/?$'), function (req, res) {
        const sec = store.getSection(req.params[0].toLowerCase() + 'Running');
        res.status(200).json({ running: sec.running === true });
    });
    app.post(new RegExp('^/ShadowPlay/v\\.1\\.0/(' + FEATURES + ')/Running/?$'), function (req, res) {
        // engine publish — the page never POSTs this; the REST client does.
        // Production pushes NO channel for Running (the page polls) — the
        // store is the only state change here.
        const body = (req.body && typeof req.body === 'object') ? req.body : {};
        const running = body.running === true;
        store.storeSection(req.params[0].toLowerCase() + 'Running', { running: running, at: new Date().toISOString() });
        res.status(200).json({ running: running });
    });

    app.get('/ShadowPlay/v.1.0/Broadcast/Enable', function (req, res) {
        res.status(200).json({ status: store.getSection('broadcastEnable').status === true });
    });
    app.post('/ShadowPlay/v.1.0/Broadcast/Enable', function (req, res) {
        const body = (req.body && typeof req.body === 'object') ? req.body : {};
        const status = body.status === true;
        store.storeSection('broadcastEnable', { status: status, changedAt: new Date().toISOString() });
        // production parity: broadcast mode 2 state flips emit
        // /Broadcast/Enable {status} (NvShadowPlayAPI.js:2786-2796)
        socket.emitChannel('/ShadowPlay/v.1.0/Broadcast/Enable', { status: status });
        res.status(200).json({ status: status });
    });
    app.post('/ShadowPlay/v.1.0/Broadcast/Pause', function (req, res) {
        // page payload {pause:bool}; the production paused-state channel
        // carries {status} (true = paused, false = resumed) — modes
        // 0x02|0x04 / 0x41 (NvShadowPlayAPI.js:2797-2804)
        const body = (req.body && typeof req.body === 'object') ? req.body : {};
        const paused = body.pause === true;
        store.storeSection('broadcastPause', { paused: paused, changedAt: new Date().toISOString() });
        socket.emitChannel('/ShadowPlay/v.1.0/Broadcast/Pause', { status: paused });
        res.status(200).json({ status: paused });
    });
    app.get('/ShadowPlay/v.1.0/Broadcast/Running', function (req, res) {
        res.status(200).json({ running: store.getSection('broadcastEnable').status === true });
    });
    app.get('/ShadowPlay/v.1.0/Broadcast/Support', function (req, res) {
        res.status(200).json({ support: true });
    });
    app.get('/ShadowPlay/v.1.0/Broadcast/LastProvider', function (req, res) {
        res.status(200).json({ lastprovider: 1 });
    });
    app.get('/ShadowPlay/v.1.0/Broadcast/Provider', function (req, res) {
        res.status(200).json(sectionOr('broadcastProvider', { provider: 'AlwaysAsk' }));
    });
    app.post('/ShadowPlay/v.1.0/Broadcast/Provider', function (req, res) {
        if (req.body && typeof req.body === 'object') store.storeSection('broadcastProvider', req.body);
        res.status(200).json({});
    });

    // ── screenshot / desktop capture / webcam / audio / mic ─────────
    app.get('/ShadowPlay/v.1.0/Screenshot/Support', function (req, res) {
        res.status(200).json({ support: true });
    });

    app.get('/ShadowPlay/v.1.0/DesktopCapture/Support', function (req, res) {
        res.status(200).json({ support: true });
    });
    app.get('/ShadowPlay/v.1.0/DesktopCapture/Support/Reason', function (req, res) {
        res.status(200).json({ support: true });
    });
    app.get('/ShadowPlay/v.1.0/DesktopCapture/Enable', function (req, res) {
        res.status(200).json(sectionOr('desktopCapture', { enable: true }));
    });
    app.post('/ShadowPlay/v.1.0/DesktopCapture/Enable', function (req, res) {
        if (req.body && typeof req.body === 'object') store.storeSection('desktopCapture', req.body);
        res.status(200).json({});
    });

    app.get('/ShadowPlay/v.1.0/Webcam/Present', function (req, res) {
        res.status(200).json({ present: false });
    });
    app.get('/ShadowPlay/v.1.0/Webcam/Enable', function (req, res) {
        res.status(200).json({ status: false });
    });
    app.post('/ShadowPlay/v.1.0/Webcam/Enable', function (req, res) {
        res.status(200).json({ status: false });
    });

    app.get('/ShadowPlay/v.1.0/Audio', function (req, res) {
        res.status(200).json(sectionOr('audio', { mode: 'both' }));
    });
    app.post('/ShadowPlay/v.1.0/Audio', function (req, res) {
        if (req.body && typeof req.body === 'object') store.storeSection('audio', req.body);
        res.status(200).json({});
    });

    app.get('/ShadowPlay/v.1.0/Microphone/Present', function (req, res) {
        res.status(200).json({ present: 0 });
    });

    // ── highlights / SDK ────────────────────────────────────────────
    app.get('/ShadowPlay/v.1.0/Highlights/Session', function (req, res) {
        res.status(200).json({ active: false });
    });
    app.get('/SDK/v.1.0/Highlights/Active', function (req, res) {
        res.status(200).json({ active: false });
    });
    app.get('/SDK/v.1.0/Instance', function (req, res) {
        res.status(200).json({ active: false });
    });

    // ── real-backend error mirrors (the page takes its normal error
    //    path on these — keep the exact shapes) ───────────────────────
    app.get('/ShadowPlay/v.1.0/InstantReplay/BufferLength', function (req, res) {
        res.status(500).json({ type: 'Error', code: -2147024809, codeText: 'Unknown/Internal error' });
    });
    app.get('/ShadowPlay/v.1.0/Record/Concurrency/:type', function (req, res) {
        res.status(200).json({ supported: true });
    });
    app.post('/ShadowPlay/v.1.0/Record/Concurrency/Manual', function (req, res) {
        res.status(500).json({
            type: 'Error', code: 3, codeText: 'Invalid argument',
            message: 'Unknown concurrency type'
        });
    });

    // ── hotkeys ─────────────────────────────────────────────────────
    // GET  /Hotkey/<name>  → {keys:[vk...]} (store → defaults → [0])
    // POST /Hotkey/<name>  → store the binding {keys:[...]}
    // POST /Hotkey/Toggle  → hotkey-listener wire-through: edge-debounced
    //                        WindowState{overlayToggle} emission
    app.get('/ShadowPlay/v.1.0/Hotkey/:name', function (req, res) {
        const hk = String(req.params.name || '').toLowerCase();
        if (hk === 'monitor') {
            // production: GET monitor reads the hotkey-monitor state
            // (HotKeyMonitor(doReply, false) — NvShadowPlayAPI.js:2494-2520)
            const mon = store.getSection('hotkey:monitor');
            res.status(200).json({ enabled: mon.enabled === true });
            return;
        }
        const sec = store.getSection('hotkey:' + hk);
        if (sec.keys) { res.status(200).json(sec); return; }
        const keys = defaults.HOTKEY_DEFAULTS[hk];
        res.status(200).json({ keys: keys ? keys : [0] });
    });
    app.post('/ShadowPlay/v.1.0/Hotkey/:name', function (req, res) {
        const hk = String(req.params.name || '').toLowerCase();
        if (hk === 'toggle' || hk === 'overlaytoggle') {
            const emitted = socket.hotkeyToggle(hk, 'overlayToggle');
            // production parity: hotkey presses also surface on the Hotkey
            // channel (payload is the native passthrough in production —
            // we emit the identity we have; representative shape)
            socket.emitHotkey({ hotkeyName: hk });
            res.status(200).json(emitted ? {} : { suppressed: true });
            return;
        }
        if (hk === 'monitor') {
            // production: both GET and POST monitor read the monitor state
            // (HotKeyMonitor(doReply, false) — NvShadowPlayAPI.js:2505-2520)
            const sec = store.getSection('hotkey:monitor');
            res.status(200).json({ enabled: sec.enabled === true });
            return;
        }
        if (hk === 'dynamictoggle') {
            // production has no DynamicToggle binding: POST /Hotkey/DynamicToggle
            // matches the /:hk param route and the native layer rejects the
            // unknown name (replyWithError path). Mirror that error floor —
            // the dead channel must not answer 200.
            res.status(500).json({
                type: 'Error', code: -1, codeText: 'Unknown/Internal error',
                message: 'Unknown hotkey: DynamicToggle'
            });
            return;
        }
        if (req.body && typeof req.body === 'object' && req.body.keys) {
            store.storeSection('hotkey:' + hk, req.body);
            ctx.logger.info('hotkey saved: ' + hk);
        }
        res.status(200).json({});
    });

    // ── OSC lifecycle handshakes (HOST.md contract) ─────────────────
    app.get('/ShadowPlay/v.1.0/OSC/Init', function (req, res) {
        res.status(200).json({});
    });
    app.post('/ShadowPlay/v.1.0/OSC/MainView', function (req, res) {
        res.status(200).json({});
    });
    app.post('/ShadowPlay/v.1.0/Osc', function (req, res) {
        // ready:{true} — mirror the native emit so listeners see the same
        // notification frame
        const body = (req.body && typeof req.body === 'object') ? req.body : {};
        socket.emitChannel('/ShadowPlay/v.1.0/Osc', body);
        res.status(200).json({});
    });

    // ── DisplayOsc POST echoes (production §A.2: content passthrough) ──
    // The production bridge broadcasts the POST body verbatim onto the
    // matching DisplayOsc channel (NvShadowPlayAPI.js:1058-1097).
    app.post('/ShadowPlay/v.1.0/OpenOscPreferences', function (req, res) {
        const body = (req.body && typeof req.body === 'object') ? req.body : {};
        socket.emitDisplayOsc('Preferences', body);
        res.status(200).json({});
    });
    app.post('/ShadowPlay/v.1.0/OpenOscState', function (req, res) {
        const body = (req.body && typeof req.body === 'object') ? req.body : {};
        socket.emitDisplayOsc('State', body);
        res.status(200).json({});
    });
    app.post('/ShadowPlay/v.1.0/OscNotification', function (req, res) {
        const body = (req.body && typeof req.body === 'object') ? req.body : {};
        socket.emitDisplayOsc('Notification', body);
        res.status(200).json({});
    });

    // ── Launch (genuine NvShadowPlayAPI.js:1157/1181 — GET state + POST) ──
    // The osc boot path requests this; the 404 was the only boot error in
    // the CDP log. Standalone: no launch pipeline — answer the empty state.
    app.post('/ShadowPlay/v.1.0/Launch', function (req, res) {
        res.status(200).json({});
    });
    app.get('/ShadowPlay/v.1.0/Launch', function (req, res) {
        res.status(200).json({});
    });

    // ── misc settings surfaces ──────────────────────────────────────
    app.get('/Settings/v.1.0/Language', function (req, res) {
        res.status(200).json(sectionOr('language', { language: '' }));
    });
    app.post('/Settings/v.1.0/Language', function (req, res) {
        if (req.body && typeof req.body === 'object') {
            store.storeSection('language', req.body);
            // production parity: POST persists then broadcasts
            // {language} (index.js:654-660)
            socket.emitChannel('/Settings/v.1.0/Language', { language: req.body.language });
        }
        res.status(200).json({});
    });

    app.get('/Localization/v.1.0/SupportedLocales', function (req, res) {
        res.status(404).json({});
    });

    app.get('/FramerateLimiter/v.0.1/state', function (req, res) {
        // v.0.1 has NO "supported" field (that is v.1.0/2) — the page boots
        // through this exact shape
        res.status(200).json({ enabled: false, value: 0 });
    });
};

function path_videos() {
    const os = require('os');
    const path = require('path');
    const home = os.homedir ? os.homedir() : (process.env.USERPROFILE || 'C:\\Users\\Public');
    return path.join(home, 'Videos');
}
function tmpdir() {
    const os = require('os');
    return os.tmpdir ? os.tmpdir() : process.env.TEMP;
}
