'use strict'
// routes/duluka.js — the Duluka account + panel surfaces and PiplConfig.
//
// Account provider (DulukaAPI.js semantics, proven on Intel — the page
// treats 200+token as "logged in", 401 as logged out):
//   GET /Account/v.1.0/UserToken
//   POST /Account/v.1.0/UserToken        {token, user} — login bridge write
//   POST /Account/v.1.0/Logout
//   GET/POST /Account/v.1.0/PrivacySettings
//   POST /Account/v.1.0/RefreshPrivacySettings
//   socket  /Account/v.1.0/GetState → /Account/v.1.0/state
//
// Duluka panel (DulukaModsAPI.js semantics):
//   GET  /Duluka/v.1.0/State
//   POST /Duluka/v.1.0/Login             {token?, user?}
//   POST /Duluka/v.1.0/Logout
//   GET  /Duluka/v.1.0/ServerHealth      — reachability probe of the Duluka
//                                          Server (jarvis) — diagnostic
//
// Duluka Capture page (DulukaCaptureAPI.js semantics):
//   GET/POST /DulukaCapture/v.1.0/settings
//   POST /DulukaCapture/v.1.0/reset
//
// PiplConfig — jarvis.server points at the Duluka Server (:5115):
//   GET  /PiplConfig/v.1.0/data
//   POST /PiplConfig/v.1.0/data          → update + socket push
//
// Misc boot calls that used to error on standalone:
//   POST /Applications/v.1.0/SetLocalizedEndpoints
//   GET  /Applications/v.1.0/            — [] (real floor)
//   GET/POST /Settings/v.1.0/RewardsNotificationPreference

const http = require('http');
const https = require('https');
const defaults = require('../lib/defaults');

const CAPTURE_DEFAULTS = {
    engine: 'ffmpeg',            // ffmpeg | obs | gpu | apitest
    preset: 'nvidia-medium',     // nvidia-low | nvidia-medium | nvidia-high | my-maximum | custom
    framerate: 60,
    resolution: 'In-game',
    bitrateBps: 50000000,
    apiCapture: 'ffmpeg',
    codeEncoder: 'h264_nvenc',
    command: ''
};

function BuildCommand(state) {
    if (state.engine !== 'ffmpeg') return '';
    const fps = state.framerate || 60;
    const rate = Math.round((state.bitrateBps || 50000000) / 1000) + 'k';
    const enc = state.codeEncoder || 'h264_nvenc';
    return 'ffmpeg -f gdigrab -framerate ' + fps + ' -i desktop ' +
        '-c:v ' + enc + ' -b:v ' + rate + ' -preset ' + state.preset.replace('nvidia-', '') +
        ' -y duluka-capture.mp4';
}

module.exports = function dulukaRoutes(app, ctx) {
    const store = ctx.store;
    const socket = ctx.socket;

    function account() { return store.getSection('account'); }

    // Production parity (NvAccountAPI.js:555-566): the native layer pushes
    // the stored session over the UserToken channel once at startup — the
    // osc boot resolve consumes THAT push (not the HTTP GET). Without a
    // boot-time push the resolve reports "user session (rejection)".
    if (ctx.socket && ctx.socket.io) {
        ctx.socket.io.on('connection', function () {
            setImmediate(function () { pushAccount(); });
        });
    }

    function pushAccount() {
        const a = account();
        // production parity (NvAccountAPI.js:555-565): the UserToken channel
        // carries {userToken, userInfo} where userInfo is a JSON STRING —
        // the page runs JSON.parse(sessionData.userInfo) and reads
        // userInfo.userId; an object here throws inside the page and the
        // boot resolve reports "user session (rejection)".
        const info = {
            userId: (a.user && (a.user.userId || a.user.id)) || (a.loggedIn ? 'duluka-user' : 'guest-standalone'),
            displayName: (a.user && (a.user.displayName || a.user.name)) || (a.loggedIn ? 'Duluka user' : 'Guest'),
            deviceId: 'standalone',
            userToken: a.token || 'guest-standalone'
        };
        socket.emitChannel('/Account/v.1.0/UserToken', {
            userToken: info.userToken,
            userInfo: JSON.stringify(info),
            userId: info.userId
        });
        socket.emitChannel('/Duluka/v.1.0/state', { loggedIn: !!a.loggedIn, user: a.user || null });
    }
    function pushDulukaState() {
        const a = account();
        socket.emitChannel('/Duluka/v.1.0/state', { loggedIn: !!a.loggedIn, user: a.user || null });
    }
    function pushCaptureState() {
        socket.emitChannel('/DulukaCapture/v.1.0/state', captureState());
    }

    function captureState() {
        const merged = Object.assign({}, CAPTURE_DEFAULTS, store.getSection('dulukaCapture'));
        merged.command = BuildCommand(merged);
        return merged;
    }

    // ── Account ─────────────────────────────────────────────────────
    // Production wire shape (NvAccountAPI.js): the UserToken answer carries
    // {userToken, userInfo} where userInfo is a JSON STRING containing
    // userId — UserTokenCallback parses it and hands the userId to the page
    // (NvAccountAPI.js:555-565). The osc boot resolve (main/resolve/
    // hardwareInfo) hard-requires that userId: a 401 or a wrong shape leaves
    // the #/base ui-view EMPTY (the "black screen", CDP-verified 2026-09-25).
    function GuestAccount() {
        const guestInfo = {
            userId: 'guest-standalone',
            displayName: 'Guest',
            deviceId: 'standalone',
            userToken: 'guest-standalone',
            dataTracking: {
                trackTechnicalData: { level: 0 },
                trackBehavioralData: { level: 0 }
            }
        };
        return {
            userToken: 'guest-standalone',
            userInfo: JSON.stringify(guestInfo),
            userId: guestInfo.userId
        };
    }
    app.get('/Account/v.1.0/UserToken', function (req, res) {
        const a = account();
        if (!a.loggedIn || !a.token) {
            // Guest-mode parity shim (2026-09-25, CDP-verified on the Intel
            // machine): the osc boot resolve hard-requires a user id — a 401
            // or a token without userInfo.userId makes the uiRouter resolve
            // reject and the #/base ui-view stays EMPTY (the "black screen").
            // A deterministic guest account lets the page boot; a real Duluka
            // login still takes precedence below.
            res.status(200).json(GuestAccount());
            return;
        }
        const info = {
            userId: (a.user && (a.user.userId || a.user.id)) || 'duluka-user',
            displayName: (a.user && (a.user.displayName || a.user.name)) || 'Duluka user',
            deviceId: 'duluka',
            userToken: a.token
        };
        res.status(200).json({
            userToken: a.token,
            userInfo: JSON.stringify(info),
            userId: info.userId
        });
    });

    app.post('/Account/v.1.0/UserToken', function (req, res) {
        const body = (req.body && typeof req.body === 'object') ? req.body : {};
        // production wire: {userToken, userInfo} (NvAccountAPI.js:334-357);
        // the Duluka login bridge's {token, user} stays accepted.
        const token = body.userToken !== undefined ? body.userToken : body.token;
        if (token) {
            store.storeSection('account', {
                token: token,
                user: body.userInfo || body.user || null,
                loggedIn: true
            });
            pushAccount();
            ctx.logger.info('account: token stored (login bridge)');
        }
        res.status(200).json({});
    });

    app.post('/Account/v.1.0/Logout', function (req, res) {
        store.storeSection('account', { loggedIn: false, token: null, user: null });
        pushAccount();
        res.status(200).json({});
    });

    app.get('/Account/v.1.0/PrivacySettings', function (req, res) {
        const a = account();
        res.status(200).json(a.privacy || { dataCollection: false, personalization: false });
    });
    app.post('/Account/v.1.0/PrivacySettings', function (req, res) {
        if (req.body && typeof req.body === 'object') {
            const a = account();
            store.storeSection('account', {
                privacy: Object.assign({}, a.privacy || {}, req.body)
            });
            // production parity: the consent native callback emits the
            // consent data verbatim (NvAccountAPI.js:568-572)
            socket.emitChannel('/Account/v.1.0/PrivacySettings', req.body);
        }
        res.status(200).json({});
    });
    app.post('/Account/v.1.0/RefreshPrivacySettings', function (req, res) {
        res.status(200).json({});
    });

    // ── Duluka panel ────────────────────────────────────────────────
    app.get('/Duluka/v.1.0/State', function (req, res) {
        const a = account();
        res.status(200).json({ loggedIn: !!a.loggedIn, user: a.user || null, provider: 'duluka' });
    });

    app.post('/Duluka/v.1.0/Login', function (req, res) {
        const body = (req.body && typeof req.body === 'object') ? req.body : {};
        store.storeSection('account', {
            token: body.token || ('local-' + Date.now()),
            user: body.user || { displayName: 'Duluka User' },
            loggedIn: true
        });
        pushDulukaState();
        const a = account();
        res.status(200).json({ ok: true, user: a.user });
    });

    app.post('/Duluka/v.1.0/Logout', function (req, res) {
        store.storeSection('account', { loggedIn: false, token: null, user: null });
        pushDulukaState();
        res.status(200).json({ ok: true });
    });

    // reachability probe of the Duluka Server (jarvis) — diagnostic only,
    // the page does not depend on it
    app.get('/Duluka/v.1.0/ServerHealth', function (req, res) {
        probeServer(ctx.cfg.dulukaServer + '/beta', 2500, function (err, status) {
            res.status(200).json({
                server: ctx.cfg.dulukaServer,
                reachable: !err,
                status: status || null,
                error: err ? String(err.message || err) : null
            });
        });
    });

    // ── Duluka Capture page ─────────────────────────────────────────
    app.get('/DulukaCapture/v.1.0/settings', function (req, res) {
        res.status(200).json(captureState());
    });
    app.post('/DulukaCapture/v.1.0/settings', function (req, res) {
        if (req.body && typeof req.body === 'object') {
            const cur = Object.assign({}, CAPTURE_DEFAULTS, store.getSection('dulukaCapture'));
            for (const k of Object.keys(CAPTURE_DEFAULTS)) {
                if (req.body[k] !== undefined) cur[k] = req.body[k];
            }
            store.replaceSection('dulukaCapture', cur);
            pushCaptureState();
        }
        res.status(200).json(captureState());
    });
    app.post('/DulukaCapture/v.1.0/reset', function (req, res) {
        store.replaceSection('dulukaCapture', Object.assign({}, CAPTURE_DEFAULTS));
        pushCaptureState();
        res.status(200).json(captureState());
    });

    // ── jarvis emulation (standalone) ───────────────────────────────
    // When pipl points jarvis.server at THIS backend, the page resolves its
    // boot session against us (real flow: NvAccountAPI.js:220-248 POSTs
    // /api/1/authentication/client/login with Basic auth(userToken:)).
    // A guest session here closes the last boot gate on standalone machines.
    app.post('/api/1/authentication/client/login', function (req, res) {
        res.status(200).json({
            sessionToken: 'guest-session-standalone',
            userId: 'guest-standalone',
            userName: 'Guest',
           Correlation: undefined,
            correlationId: 'guest-standalone'
        });
    });
    app.get('/api/1/authentication/client/login', function (req, res) {
        res.status(200).json({
            sessionToken: 'guest-session-standalone',
            userId: 'guest-standalone'
        });
    });

    // ── PiplConfig (jarvis.server → Duluka Server) ──────────────────
    function piplBody() {
        const floorPipl = defaults.PIPL_FLOOR;
        const stored = store.getSection('piplConfig');
        const body = JSON.parse(JSON.stringify(floorPipl)); // deep-enough copy
        body.daysToExpire = stored.daysToExpire !== undefined ? stored.daysToExpire : (body.daysToExpire || 1);
        if (!body.configData) body.configData = {};
        // Empty string is a VALID posture (official nodejs\config.json ships
        // jarvis.server: "" — standalone, no jarvis): only an UNSET value
        // falls back to the Duluka default. A `||` here silently resurrects
        // a dead jarvis URL the user explicitly cleared.
        body.configData.jarvis = {
            server: stored.jarvisServer !== undefined ? stored.jarvisServer : ctx.cfg.dulukaServer,
            // the page builds its jarvis session from these — clientId is
            // REQUIRED (PrivacySettings?clientId=... ; official value from
            // nodejs\config.json)
            clientId: '135333107684344109',
            clientDescription: 'NVIDIA Web Helper',
            userConsentRefreshWaitMins: 1440
        };
        return body;
    }

    app.get('/PiplConfig/v.1.0/data', function (req, res) {
        res.status(200).json(piplBody());
    });
    app.post('/PiplConfig/v.1.0/data', function (req, res) {
        if (req.body && typeof req.body === 'object') {
            store.storeSection('piplConfig', {
                daysToExpire: req.body.daysToExpire,
                jarvisServer: (req.body.configData && req.body.configData.jarvis)
                    ? req.body.configData.jarvis.server : undefined
            });
            socket.emitChannel('/PiplConfig/v.1.0/update', piplBody());
        }
        res.status(200).json({});
    });

    // ── misc boot calls ─────────────────────────────────────────────
    app.post('/Applications/v.1.0/SetLocalizedEndpoints', function (req, res) {
        res.status(200).json({});
    });
    app.get('/Applications/v.1.0/', function (req, res) {
        res.status(200).json([]);
    });
    app.get('/Applications/v.1.0', function (req, res) {
        res.status(200).json([]);
    });

    app.get('/Settings/v.1.0/RewardsNotificationPreference', function (req, res) {
        const a = account();
        res.status(200).json(a.rewards || {});
    });
    app.post('/Settings/v.1.0/RewardsNotificationPreference', function (req, res) {
        if (req.body && typeof req.body === 'object') {
            store.storeSection('account', { rewards: req.body });
        }
        res.status(200).json({});
    });

    // ── socket surface: live account state on demand ────────────────
    socket.io.on('connection', function (sock) {
        sock.on('/Account/v.1.0/GetState', function () {
            const a = account();
            sock.emit('/Account/v.1.0/state', {
                loggedIn: !!a.loggedIn,
                user: a.user || null,
                provider: 'duluka'
            });
        });
    });
};

function probeServer(url, timeoutMs, cb) {
    try {
        const mod = url.indexOf('https:') === 0 ? https : http;
        const req = mod.get(url, { timeout: timeoutMs }, function (r) {
            r.resume();
            cb(null, r.statusCode);
        });
        req.on('timeout', function () { req.destroy(new Error('timeout')); });
        req.on('error', function (err) { cb(err); });
    } catch (err) {
        cb(err);
    }
}
