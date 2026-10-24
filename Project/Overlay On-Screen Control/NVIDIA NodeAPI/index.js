/* Copyright (c) 2015-2023, NVIDIA CORPORATION.  All rights reserved.
 *
 * NVIDIA CORPORATION and its licensors retain all intellectual property
 * and proprietary rights in and to this software, related documentation
 * and any modifications thereto.  Any use, reproduction, disclosure or
 * distribution of this software and related documentation without an express
 * license agreement from NVIDIA CORPORATION is strictly prohibited.
 */

'use strict'

////////////////////////////////////////////////////////////////////////////////
// Common utility and checks                                                  //
////////////////////////////////////////////////////////////////////////////////

if (false) { // standalone: run on any modern node
    var err = 'nodejs version 11.13.0 is required, you are using ' + process.version;
    throw err;
}

process.env.UV_THREADPOOL_SIZE = 64;

var nvUtil = require('./shims/NvUtil.js');
nvUtil.ClaimSingleInstance();

var fs = require('fs');
var path = require('path');
var localGpuRequestGuard = require('./localGpuRequestGuard.js');
var containerTestMode = false;
var containerControlApiPort = 59012;
var captureRuntimeEnabled = false;
var captureRuntimeApiPort = 59077;
var containerConfigFound = false;
var containerConfigError = null;
try {
    var configDir = __dirname;
    for (var configDepth = 0; configDepth < 10; configDepth++) {
        var containerConfigPath = path.join(configDir, 'NVIDIA Config', 'nvcontainer.json');
        if (fs.existsSync(containerConfigPath)) {
            var containerConfig = JSON.parse(fs.readFileSync(containerConfigPath, 'utf8'));
            containerTestMode = !!(containerConfig.testMode && containerConfig.testMode.enabled);
            var configuredControlPort = Number(containerConfig.controlApiPort);
            if (Number.isInteger(configuredControlPort) && configuredControlPort > 0 && configuredControlPort <= 65535) {
                containerControlApiPort = configuredControlPort;
            }
            captureRuntimeEnabled = !!(containerConfig.captureRuntime && containerConfig.captureRuntime.enabled);
            var configuredCapturePort = Number(containerConfig.captureRuntime && containerConfig.captureRuntime.apiPort);
            if (Number.isInteger(configuredCapturePort) && configuredCapturePort > 0 && configuredCapturePort <= 65535) {
                captureRuntimeApiPort = configuredCapturePort;
            }
            containerConfigFound = true;
            break;
        }
        var parentDir = path.dirname(configDir);
        if (parentDir === configDir) break;
        configDir = parentDir;
    }
} catch (e) {
    containerConfigError = e;
}
process.env.NV_CONTAINER_TEST_MODE = containerTestMode ? '1' : '0';
process.env.NV_CONTAINER_CONTROL_API_PORT = String(containerControlApiPort);
process.env.NV_CAPTURE_RUNTIME_ENABLED = captureRuntimeEnabled ? '1' : '0';
process.env.NV_CAPTURE_RUNTIME_API_PORT = String(captureRuntimeApiPort);
var logger = require('./Logger.js')(nvUtil, GetNvNodeAppdataDirectoryPath() + '\\nvnode.log');
if (containerConfigError) {
    logger.error('Unable to read NvContainer configuration; using non-test defaults: ' + containerConfigError.message);
} else if (!containerConfigFound) {
    logger.info('NvContainer configuration not found; using non-test defaults');
}

var openSslConfigPath = GetNvNodeAppdataDirectoryPath() + '\\openssl.cfg';
process.env.OPENSSL_CONF = openSslConfigPath;

function SendNodeJSExceptionFeedback(message) {
    if (NvBackendAPI && typeof NvBackendAPI.addNodeJSCrashFeedbackSync === "function") {
        var err = NvBackendAPI.addNodeJSCrashFeedbackSync(message);
        if (err) {
            logger.error("Failed to send automatic feedback about NodeJS exception, reason: " + err);
        } else {
            logger.info("Successfully sent automatic feedback about NodeJS exception");
        }
    }
    else {
        logger.error("Failed to send automatic feedback about NodeJS exception, NvBackend plugin is not loaded or does not have AddFeedback functionality.");
    }
}

function OnUnhandledError(err) {
    try {
        if (err) {
            if (err.stack) {
                logger.error(err.stack);
            }
            else {
                logger.error(err);
            }
            SendNodeJSExceptionFeedback(err.toString());
        } else {
            SendNodeJSExceptionFeedback('undefined error');
        }
    } catch (e) {
        logger.error(e);
    }
    Shutdown(1);
}

process.on('uncaughtException', function (err) {
    logger.error("uncaughtException handler triggered!");
    OnUnhandledError(err);
})

const localSystemContainerServiceName = 'NvContainerLocalSystem';

function GfeIsInStandbyMode() {

    const SERVICE_AUTO_START = 2;
    const SERVICE_STOPPED = 1;

    let status = nvUtil.GetSystemServiceStatus(localSystemContainerServiceName);

    if (status.state !== SERVICE_STOPPED) {
        return false;
    }

    if (status.startupType === SERVICE_AUTO_START) {
        return false;
    }

    var path = GetNvidiaAppdataDirectoryPath() + '\\NVIDIA GeForce Experience\\CefCache';
    try {
        var st = fs.statSync(path);
        return !st.isDirectory();
    }
    catch (err) {
        return (err && err.code === 'ENOENT');
    }
}

if (GfeIsInStandbyMode()) {
    logger.infoSync('nodejs is exiting as GFE is in standby mode');
    Shutdown();
}

logger.info('Starting NvContainer Local System container...');
try {
    nvUtil.StartSystemService(localSystemContainerServiceName, 30000);
}
catch (err) {
    logger.error(err);
    Shutdown();
}

function GetNvidiaAppdataDirectoryPath() {
    return nvUtil.GetLocalAppdataPath() + '\\NVIDIA Corporation';
}

function GetSelfUpdateStatusFilePath() {
    var path = nvUtil.GetProgramDataPath() + '\\NVIDIA Corporation\\Downloader';

    try {
        fs.mkdirSync(path, { recursive: true });
    } catch (e) {
        return null;
    }

    return path;
}

function GetNvNodeAppdataDirectoryPath() {
    var path = GetNvidiaAppdataDirectoryPath();

    try {
        fs.mkdirSync(path);
    } catch (e) {
        if (e.code !== 'EEXIST') throw e;
    }

    path = path + '\\NvNode';
    try {
        fs.mkdirSync(path);
    } catch (e) {
        if (e.code !== 'EEXIST') throw e;
    }

    return path;
}

function GetNvNodeProgramDataDirectoryPath() {
    var path = nvUtil.GetProgramDataPath() + '\\NVIDIA Corporation\\NvNode';

    try {
        fs.mkdirSync(path, { recursive: true });
    } catch (e) {
        return null;
    }

    return path;
}

logger.info('Loading fast-boot dependency...');
var fastboot = require('./shims/fast-boot.js');
fastboot.start({ cacheFile: './module-locations-cache.json' });
logger.info('fast-boot ready');

logger.info('Loading ExpressJS dependency...');
var app = require('./node_modules/express/index.js')();
// Enforce local Host/Origin/Referer checks before all static and API routes.
// Leave NVIDIA's legacy cookie mode unchanged for OSC/CEF compatibility.
var localApiSecurity = require('./localApiSecurity.js');
app.use(localApiSecurity.httpMiddleware);
// ReShade Manager: guided official setup and read-only game inspection.
// Never copies game DLLs or launches an installer from an HTTP endpoint.
require('./shims/reshade-installer-route.js').mount(app);

// GPU runtime and Game Watcher event validation for the loopback ContainerEvents lane.
var gpuRuntimeValidation = require('./gpuRuntimeValidation.js');
var isValidGpuRuntimeState = gpuRuntimeValidation.isValidGpuRuntimeState;
var gameWatcherValidation = require('./gameWatcherValidation.js');

app.get('/nvcamera-test-mode.js', function (req, res) {
    res.setHeader('Cache-Control', 'no-store');
    res.setHeader('Content-Type', 'application/javascript; charset=utf-8');
    res.end('window.NV_CONTAINER_TEST_MODE = ' + (containerTestMode ? 'true' : 'false') + ';');
});

// OSC CSS/JS mods in osc/mods — the host injects them when the page loads.
app.get('/mods/list', function (req, res) {
    var dir = require('path').join(__dirname, 'osc', 'mods');
    var fs = require('fs');
    res.setHeader('Cache-Control', 'no-store');
    fs.readdir(dir, function (err, files) {
        if (err) { res.json([]); return; }
        // Serve only the source-owned mod manifest. The Build tree may retain
        // retired CSS/JS files; auto-loading every extension would reactivate them.
        var allowedMods = {
            'ansel-game-filter.js': true,
            'base.css': true,
            'folder-browser-navigation.js': true,
            'gallery-media-source.js': true,
            'gpu-profiles.js': true,
            'gpu-profiles.css': true,
            'gpu-status-panel.css': true,
            'gpu-status-panel.js': true,
            'notification-settings.js': true,
            'overlays.css': true,
            'preferences.css': true,
            'privacy-control.css': true,
            'reshade-manager.css': true,
            'reshade-manager.js': true,
            'providers.css': true,
            'notifications.css': true,
            'video-capture-options.css': true,
            'video-capture-options.js': true
        };
        files = files.filter(function (f) {
            return allowedMods[f.toLowerCase()] === true;
        }).sort();
        var remaining = files.length;
        var mods = [];
        if (!remaining) { res.json(mods); return; }
        files.forEach(function (name) {
            fs.stat(require('path').join(dir, name), function (statErr, stat) {
                if (!statErr && stat.isFile()) {
                    mods.push({
                        name: name,
                        version: String(stat.mtimeMs) + '-' + String(stat.size)
                    });
                }
                if (--remaining === 0) {
                    mods.sort(function (a, b) { return a.name.localeCompare(b.name); });
                    res.json(mods);
                }
            });
        });
    });
});

// OUR LANE: serve osc page ของเราก่อน route อื่น (same origin = หมดปัญหา CORS/port)
// ★ 2026-10-08 OWNER: osc ย้ายมาอยู่ใต้ backend (แบบแท้ — node เป็นเจ้าของหน้า)
app.use(require('express').static(require('path').join(__dirname, 'osc')));
logger.info('ExpressJS ready');


logger.info('Loading HTTP dependency...');
var http = require('http');
logger.info('Creating HTTP server...');
var httpServer = http.createServer(app);

    // NvContainer owns hotkey dispatch; it publishes the resulting OSC event here.
    var containerHotkeyEvents = require('./shims/container-hotkey-event.js');
    app.post('/Backend/v.1.0/ContainerEvents', function (req, res) {
        var remote = req.socket && req.socket.remoteAddress;
        if (remote !== '127.0.0.1' && remote !== '::1' && remote !== '::ffff:127.0.0.1') {
            res.writeHead(403, { 'Content-Type': 'application/json' });
            res.end(JSON.stringify({ error: 'loopback only' }));
            return;
        }
        var body = '';
        req.on('data', function (chunk) {
            body += chunk;
            if (body.length > 65536) req.destroy();
        });

        req.on('end', function () {
            try {
                var payload = JSON.parse(body || '{}');
                if (!Array.isArray(payload.events) || payload.events.length > 32) {
                    res.writeHead(400, { 'Content-Type': 'application/json' });
                    res.end(JSON.stringify({ error: 'invalid events' }));
                    return;
                }
                var delivered = 0;
                var nativeToggles = [];
                payload.events.forEach(function (event) {
                    if (!event || !event.data) return;
                    if (event.name === '/ShadowPlay/v.1.0/Hotkey' &&
                        typeof event.data.hotkeyId === 'string') {
                        // The native HWND is the visibility owner, even when CEF
                        // is still at #/base. Do not also emit WindowState:
                        // the OSC Host will update the CEF route on its own.
                        var routed = containerHotkeyEvents.normalizeContainerHotkeyEvent(event);
                        if (routed.name === '/ShadowPlay/v.1.0/WindowState') {
                            nativeToggles.push(containerHotkeyEvents.toggleNativeOsc());
                        } else {
                            io.emit(routed.name, routed.data);
                            delivered++;
                        }
                        return;
                    }
                    if (event.name === '/Duluka/v1/GpuRuntimeState') {
                        if (isValidGpuRuntimeState(event.data)) {
                            io.emit(event.name, event.data);
                            delivered++;
                        }
                        return;
                    }
                    if (event.name === '/Duluka/v1/GpuPreferenceApplied') {
                        if (gpuRuntimeValidation.isValidGpuPreferenceApplied(event.data)) {
                            io.emit(event.name, event.data);
                            delivered++;
                        }
                        return;
                    }
                    if (event.name === '/Duluka/v1/GpuAdapterInventory') {
                        if (gpuRuntimeValidation.isValidGpuAdapterInventory(event.data)) {
                            io.emit(event.name, event.data);
                            delivered++;
                        }
                        return;
                    }
                    if (event.name === '/Duluka/v1/GameWatcherStatus') {
                        if (gameWatcherValidation.isValidGameWatcherStatus(event.data)) {
                            io.emit(event.name, event.data);
                            delivered++;
                        }
                        return;
                    }
                    if (event.name !== '/Duluka/v1/OBSNotification' ||
                        !/^(RECORD_STARTED|RECORD_STOPPED|RECORD_PAUSED|RECORD_RESUMED|INSTANT_REPLAY_STARTED|INSTANT_REPLAY_STOPPED|INSTANT_REPLAY_SAVED|BROADCAST_STARTED|BROADCAST_STOPPED)$/.test(event.data.selection)) return;
                    io.emit(event.name, event.data);
                    delivered++;
                });
                Promise.all(nativeToggles).then(function (results) {
                    results.forEach(function (ok) { if (ok) delivered++; });
                    var allAccepted = delivered === payload.events.length;
                    res.writeHead(allAccepted ? 200 : 503,
                        { 'Content-Type': 'application/json' });
                    res.end(JSON.stringify({ delivered: delivered,
                        nativeToggleAttempts: results.length,
                        nativeToggleFailures: results.filter(function (ok) { return !ok; }).length }));
                }).catch(function () {
                    res.writeHead(503, { 'Content-Type': 'application/json' });
                    res.end(JSON.stringify({ error: 'OSC native toggle unavailable' }));
                });
            } catch (e) {
                res.writeHead(400, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify({ error: 'invalid JSON' }));
            }
        });
    });

    // Restart remains loopback-only and requires explicit user confirmation.
    // Bound the upstream wait so a non-responsive NvContainer cannot leave CEF pending forever.
    app.post('/Duluka/v1/gpu/restart', function (req, res) {
        if (!localGpuRequestGuard.authorize(req)) {
            res.writeHead(403, { 'Content-Type': 'application/json' });
            res.end(JSON.stringify({ error: 'GPU request must originate from the local NodeAPI origin' }));
            return;
        }

        var body = '';
        req.on('data', function (chunk) {
            body += chunk;
            if (body.length > 4096) req.destroy();
        });
        req.on('end', function () {
            var payload;
            try {
                payload = JSON.parse(body || '{}');
            } catch (e) {
                res.writeHead(400, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify({ error: 'invalid JSON' }));
                return;
            }
            if (!payload || payload.confirmed !== true ||
                typeof payload.exePath !== 'string' || payload.exePath.length > 512) {
                res.writeHead(400, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify({ error: 'confirmation and exePath are required' }));
                return;
            }

            var completed = false;
            var proxyTimer;
            var proxy = require('http').request({
                host: '127.0.0.1',
                port: containerControlApiPort,
                path: '/v1/gpu/restart',
                method: 'POST',
                headers: { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body) }
            }, function (upstream) {
                var responseBody = '';
                upstream.on('data', function (chunk) { responseBody += chunk; });
                upstream.on('end', function () {
                    if (completed || res.finished) return;
                    completed = true;
                    clearTimeout(proxyTimer);
                    res.writeHead(upstream.statusCode || 502, { 'Content-Type': 'application/json' });
                    res.end(responseBody || JSON.stringify({ error: 'NvContainer returned an empty response' }));
                });
                upstream.on('error', function (error) {
                    if (completed) return;
                    completed = true;
                    clearTimeout(proxyTimer);
                    logger.error('GPU restart response failed: ' + error.message);
                    if (!res.headersSent && !res.finished) {
                        res.writeHead(502, { 'Content-Type': 'application/json' });
                        res.end(JSON.stringify({ error: 'NvContainer response failed' }));
                    }
                });
            });
            proxyTimer = setTimeout(function () {
                if (completed) return;
                completed = true;
                logger.error('GPU restart request timed out.');
                proxy.abort();
                if (!res.headersSent && !res.finished) {
                    res.writeHead(504, { 'Content-Type': 'application/json' });
                    res.end(JSON.stringify({ error: 'NvContainer request timed out' }));
                }
            }, 30000);
            res.on('close', function () {
                if (completed) return;
                completed = true;
                clearTimeout(proxyTimer);
                proxy.abort();
            });
            proxy.on('error', function (error) {
                if (completed) return;
                completed = true;
                clearTimeout(proxyTimer);
                logger.error('GPU restart request failed: ' + error.message);
                if (!res.headersSent && !res.finished) {
                    res.writeHead(502, { 'Content-Type': 'application/json' });
                    res.end(JSON.stringify({ error: 'NvContainer is unavailable' }));
                }
            });
            proxy.end(body);
        });
    });

    function proxyGpuProfileRequest(req, res, upstreamPath, method, timeoutMs) {
        if (!localGpuRequestGuard.authorize(req)) {
            res.writeHead(403, { 'Content-Type': 'application/json' });
            res.end(JSON.stringify({ error: 'GPU request must originate from the local NodeAPI origin' }));
            return;
        }

        function forward(body) {
            var headers = { 'Content-Type': 'application/json' };
            if (body) headers['Content-Length'] = Buffer.byteLength(body);
            var completed = false;
            var proxy = require('http').request({
                host: '127.0.0.1',
                port: containerControlApiPort,
                path: upstreamPath,
                method: method,
                headers: headers
            }, function (upstream) {
                var responseBody = '';
                upstream.on('data', function (chunk) { responseBody += chunk; });
                upstream.on('end', function () {
                    if (completed || res.finished) return;
                    completed = true;
                    clearTimeout(proxyTimer);
                    res.writeHead(upstream.statusCode || 502, { 'Content-Type': 'application/json' });
                    res.end(responseBody || JSON.stringify({ error: 'NvContainer returned an empty response' }));
                });
                upstream.on('error', function (error) {
                    if (completed) return;
                    completed = true;
                    clearTimeout(proxyTimer);
                    logger.error('GPU profile response failed: ' + error.message);
                    if (!res.headersSent && !res.finished) {
                        res.writeHead(502, { 'Content-Type': 'application/json' });
                        res.end(JSON.stringify({ error: 'NvContainer response failed' }));
                    }
                });
            });
            var proxyTimer = setTimeout(function () {
                if (completed) return;
                completed = true;
                logger.error('GPU profile request timed out: ' + method + ' ' + upstreamPath);
                proxy.abort();
                if (!res.headersSent && !res.finished) {
                    res.writeHead(504, { 'Content-Type': 'application/json' });
                    res.end(JSON.stringify({ error: 'NvContainer request timed out' }));
                }
            }, timeoutMs || 15000);
            res.on('close', function () {
                if (completed) return;
                completed = true;
                clearTimeout(proxyTimer);
                proxy.abort();
            });
            proxy.on('error', function (error) {
                if (completed) return;
                completed = true;
                clearTimeout(proxyTimer);
                logger.error('GPU profile request failed: ' + error.message);
                if (!res.headersSent && !res.finished) {
                    res.writeHead(502, { 'Content-Type': 'application/json' });
                    res.end(JSON.stringify({ error: 'NvContainer is unavailable' }));
                }
            });
            if (body) proxy.end(body);
            else proxy.end();
        }

        if (method === 'GET') {
            forward('');
            return;
        }
        var body = '';
        req.on('data', function (chunk) {
            body += chunk;
            if (body.length > 4096) req.destroy();
        });
        req.on('end', function () {
            forward(body);
        });
    }

    app.get('/Duluka/v1/gpu/profiles', function (req, res) {
        proxyGpuProfileRequest(req, res, '/v1/gpu/profiles', 'GET');
    });
    app.get('/Duluka/v1/gpu/running-games', function (req, res) {
        proxyGpuProfileRequest(req, res, '/v1/gpu/running-games', 'GET');
    });
    app.post('/Duluka/v1/gpu/profiles/add', function (req, res) {
        proxyGpuProfileRequest(req, res, '/v1/gpu/profiles/add', 'POST');
    });
    app.get('/Duluka/v1/gpu/manager', function (req, res) {
        proxyGpuProfileRequest(req, res, '/v1/gpu/manager', 'GET');
    });
    app.get('/Duluka/v1/gpu/file-picker/executable', function (req, res) {
        proxyGpuProfileRequest(req, res, '/v1/gpu/file-picker/executable', 'GET', 120000);
    });
    app.get('/Duluka/v1/gpu/running-profile-paths', function (req, res) {
        proxyGpuProfileRequest(req, res, '/v1/gpu/running-profile-paths', 'GET');
    });
    app.post('/Duluka/v1/gpu/profiles/save', function (req, res) {
        proxyGpuProfileRequest(req, res, '/v1/gpu/profiles/save', 'POST');
    });
    app.post('/Duluka/v1/gpu/profiles/remove', function (req, res) {
        proxyGpuProfileRequest(req, res, '/v1/gpu/profiles/remove', 'POST');
    });
    app.post('/Duluka/v1/gpu/preference/apply', function (req, res) {
        proxyGpuProfileRequest(req, res, '/v1/gpu/preference/apply', 'POST');
    });
    app.post('/Duluka/v1/gpu/preference/remove', function (req, res) {
        proxyGpuProfileRequest(req, res, '/v1/gpu/preference/remove', 'POST');
    });
    app.get('/Duluka/v1/gpu/graphics/status', function (req, res) {
        proxyGpuProfileRequest(req, res, '/v1/gpu/graphics/status', 'GET');
    });
    app.post('/Duluka/v1/gpu/graphics/open', function (req, res) {
        proxyGpuProfileRequest(req, res, '/v1/gpu/graphics/open', 'POST');
    });
    app.post('/Duluka/v1/gpu/backups/restore', function (req, res) {
        proxyGpuProfileRequest(req, res, '/v1/gpu/backups/restore', 'POST');
    });
    app.post('/Duluka/v1/gpu/runtime/check', function (req, res) {
        proxyGpuProfileRequest(req, res, '/v1/gpu/runtime/check', 'POST');
    });

    // OUR LANE: host health probe (the managed NVIDIA Web Helper.exe waits
    // for this before declaring the backend healthy — standalone addition).
    app.get('/Backend/v.1.0/health', function (req, res) {
        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ ok: true }));
    });
    // OUR LANE: hotkey fire บนพอร์ตเดียว (helper POST /?hk=<Name>) — NvShadowPlayAPINode
    //   ประกาศ global.__nvFire ไว้ (route อยู่ก่อน security cookie = helper ยิงตรง)
    var hkHandler = function (req, res) {
        var m = (req.url || '').match(/hk=([A-Za-z0-9_]+)/);
        var name = m ? m[1] : 'OpenShare';
        if (typeof global.__nvFire === 'function') {
            if (global.__nvFire(name) !== true) {
                res.writeHead(503, { 'Content-Type': 'text/plain' });
                res.end('hotkey callback is not ready');
                return;
            }
            res.writeHead(200, { 'Content-Type': 'text/plain' });
            res.end('fired ' + name);
        } else {
            res.writeHead(503, { 'Content-Type': 'text/plain' });
            res.end('fire not ready');
        }
    };
    app.get('/', hkHandler);
    app.post('/', hkHandler);
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
                        port: 59011, disableSecurity: true, securityCookie: '',
                        gfwsl: { server: 'https://gfwsl.geforce.com/' }
                    });
                } else {
                    reply = 'ECHO body=' + body.slice(0, 100);
                }
            } catch (e) { reply = 'PARSE ERR ' + e.message + ' body=' + body.slice(0, 60); }
            res.header('Content-Type', 'application/json');
            // Local same-origin OSC bridge: never opt into wildcard CORS.
            res.end(reply);
        });
    });
logger.info('HTTP ready');


logger.info('Loading Socket.IO dependency...');
var io = require('./node_modules/socket.io/lib/index.js')(httpServer);
// Engine.IO handles websocket/polling itself, outside Express middleware.
io.eio.allowRequest = localApiSecurity.socketAllowRequest;
logger.info('Socket.IO ready');

logger.info('Loading "on-finished" dependency...');
var onFinished = require('on-finished');

//
// Process each request:
// 1) Enable CORS and check security cookie.
// 2) Log requests and responses for debug purposes.
//

const securityCheckEnabled = nvUtil.IsSecurityCheckEnabled();
const securityCookie = nvUtil.GenerateRandom(16);

var nextRequestId = 1;

app.use(function (req, res, next) {
    // HTTP origin policy is enforced before routing by localApiSecurity.
    // Same-origin requests do not need CORS response headers.

    if (req.method !== 'OPTIONS' && securityCheckEnabled && req.headers.x_local_security_cookie != securityCookie) {
        logger.error('Rejecting request with invalid security cookie: ' + req.method + ' ' + req.originalUrl);
        res.writeHead(403, { 'Content-Type': 'text/html;charset=utf-8' });
        res.end('Security token is invalid');
        return;
    }

    var requestId = nextRequestId;
    nextRequestId++;

    logger.debug('Incoming request  #' + requestId + ': ' + req.method + ' ' + req.originalUrl);
    onFinished(res, function (err, res) {
        logger.debug('Response finished #' + requestId + ': ' + req.method + ' ' + req.originalUrl + ' with status ' + res.statusCode);
    });
    next();
});

// Read-only multi-engine capability bridge. No OSC UI dependency or recording commands.
require('./shims/capture-engine-status.js')(app);
require('./shims/capture-capabilities.js')(app);
require('./shims/capture-runtime-status.js')(app);

// route-floor: OSC API route facade with persisted settings and runtime state.
require('./shims/route-floor.js')(app, io);
// ★ เว็บเวอร์ชันของ OSC ของเรา — http://127.0.0.1:<port>/osc/
try {
    var oscWebDir = require('path').join(__dirname, 'osc-web');
    app.use('/osc', require('express').static(oscWebDir));
    console.log('[osc-web] serving ' + oscWebDir);
} catch (e) { console.log('[osc-web] static fail: ' + e.message); }

io.use(function (socket, next) {
    if (securityCheckEnabled && socket.handshake.query.X_LOCAL_SECURITY_COOKIE != securityCookie) {
        next(new Error('Security token is invalid'));
    }
    else {
        next();
    }
});

//
// Logging socket.io connections.
//
io.on('connection', function (socket) {
    logger.info('Socket ' + socket.id + ' connected');

    socket.on('error', function (error) {
        logger.info('Socket ' + socket.id + ' error: ' + error);
    });

    socket.on('reconnect', function () {
        logger.info('Socket ' + socket.id + ' reconnected');
    });

    socket.on('reconnecting', function () {
        logger.info('Socket ' + socket.id + ' reconnecting');
    });

    socket.on('reconnect_attempt', function () {
        logger.info('Socket ' + socket.id + ' reconnect attempt');
    });

    socket.on('reconnect_error', function () {
        logger.error('Socket ' + socket.id + ' reconnect error');
    });

    socket.on('reconnect_failed', function () {
        logger.error('Socket ' + socket.id + ' reconnect failed');
    });

    socket.on('disconnect', function () {
        logger.error('Socket ' + socket.id + ' disconnected');
    });
});


//
// NvPiplConfig is a pre-requisite of NvBackend, load it first.
//

const gfeVersion = nvUtil.GetGFEVersionSync();
const nvNodeProgramDataPath = GetNvNodeProgramDataDirectoryPath();
logger.info('Loading NvPiplConfig module...');
const NvPiplConfig = require('./NvPiplConfig')(app, io, logger, gfeVersion, nvNodeProgramDataPath);
logger.info('NvPiplConfig module loaded');

//
// NvBackend module is mandatory, preload it before waiting for LS container.
//

var NvBackendAPI;
{
    if (securityCheckEnabled) {
        nvUtil.VerifyFileSignatureSync(__dirname + '\\NvBackendAPINode.node');
    }
    logger.info('Loading NvBackendAPI module...');
    NvBackendAPI = require('./NvBackendAPI.js')(httpServer, app, io, logger, NvPiplConfig);
    logger.info('NvBackendAPI module loaded');
}

//
// Some modules that don't depend on NvContainer could be loaded while LS container is starting.
//

var NvAccountAPI;
var DriverInstallAPI;
var downloaderAPI;

let modulesPreloadError = undefined;
try {
    if (securityCheckEnabled) {
        nvUtil.VerifyFileSignatureSync(__dirname + '\\NvAccountAPINode.node');
    }
    logger.info('Loading AccountAPI module...');
    NvAccountAPI = require('./NvAccountAPI.js')(app, io, logger, SendNodeJSExceptionFeedback, NvBackendAPI, NvPiplConfig);
    logger.info('AccountAPI module loaded');

    if (securityCheckEnabled) {
        nvUtil.VerifyFileSignatureSync(__dirname + '\\DriverInstall.node');
    }
    logger.info('Loading DriverInstallAPI module...');
    DriverInstallAPI = require('./DriverInstallAPI.js')(app, io, logger);
    logger.info('DriverInstallAPI module loaded');

    if (securityCheckEnabled) {
        nvUtil.VerifyFileSignatureSync(__dirname + '\\Downloader.node');
    }
    logger.info('Loading downloaderAPI module...');
    downloaderAPI = require('./downloader.js')(app, io, logger);
    logger.info('downloaderAPI module loaded');
} catch (e) {
    // Do not throw error right away, try to keep it until reporting is up.
    logger.error(e);
    modulesPreloadError = e;
}

function FileExists(path) {
    try {
        var st = fs.statSync(path);
        return st.isFile();
    }
    catch (err) {
        return !(err && err.code === 'ENOENT');
    }
}

//
// Check signature of native modules before waiting for NvContainerLS.
//

function VerifySignatureIfFileExists(path) {
    if (FileExists(path)) {
        nvUtil.VerifyFileSignatureSync(path);
    } else {
        logger.info("Skipping signature verification as file doesn't exist: ", path);
    }
}

let signatureVerificationError = undefined;

if (securityCheckEnabled) {
    logger.info('Verifying native module signatures...');

    try {
        VerifySignatureIfFileExists(__dirname + '\\NvCameraAPINode.node');
        VerifySignatureIfFileExists(__dirname + '\\NVGalleryAPINode.node');
        VerifySignatureIfFileExists(__dirname + '\\NvGameStreamAPINode.node');
        VerifySignatureIfFileExists(__dirname + '\\NvShadowPlayAPINode.node');
        VerifySignatureIfFileExists(__dirname + '\\NvSDKAPINode.node');
        VerifySignatureIfFileExists(__dirname + '\\NvABHubAPI.node');
    } catch (e) {
        // Do not throw error right away, try to keep it until reporting is up.
        logger.error(e);
        signatureVerificationError = e;
    }
}


//
// Wait for LocalSystem container if it is not yet running.
//

try {
    logger.info('Waiting for NvContainer Local System container to start...');
    nvUtil.WaitSystemService(localSystemContainerServiceName);
    logger.info('NvContainer Local System container started');
}
catch (err) {
    logger.error(err);
    Shutdown();
}


////////////////////////////////////////////////////////////////////////////////
// Module initialization chain                                                //
////////////////////////////////////////////////////////////////////////////////

NvBackendAPI.initialize().catch(function (err) {
    NvBackendAPI = undefined;
    OnUnhandledError(err);
}).then(LoadNVIDIAModules).then(StartHTTPServer).then(RegisterShutdownCallbacks).catch(OnUnhandledError);

////////////////////////////////////////////////////////////////////////////////
// Other NVIDIA modules                                                             //
////////////////////////////////////////////////////////////////////////////////

var NvCommonTasks;
var NvAutoDriverDownload;
var NvAutoGFEDownload;

var NvGameStreamAPI;
var ShadowPlayAPI;
var NvGalleryAPI;
var NvCameraAPI;
var NvSDKAPI;
var NvAbHubAPI;

function ReportOptionalModuleLoadError(err) {
    logger.error("Optional module load error!");
    if (err.stack) {
        logger.error(err.stack);
    }
    else {
        logger.error(err);
    }

    SendNodeJSExceptionFeedback("Optional module load error: " + err.toString());
}

function LoadNVIDIAModules() {

    if (signatureVerificationError) {
        throw signatureVerificationError;
    }

    if (modulesPreloadError) {
        throw modulesPreloadError;
    }

    let promises = [];

    // NvPiplConfig is already loaded, initialize it.
    promises.push(NvPiplConfig.initialize());

    try {
        logger.info('NvAbHubAPI: Loading module...');
        NvAbHubAPI = require('./NvAbHubAPI.js')(app, io, logger);
        promises.push(NvAbHubAPI.initialize().catch(function (err) {
            logger.error("NvAbHubAPI: Initialization failed");
            NvAbHubAPI = undefined;
            ReportOptionalModuleLoadError(err);
        }));
    } catch (err) {
        logger.error("NvAbHubAPI: Module load failed");
        NvAbHubAPI = undefined;
        ReportOptionalModuleLoadError(err);
    }

	const nvSelfUpdateStatusFilePath = GetSelfUpdateStatusFilePath();
    // DownloaderAPI is already loaded, time to initialize it and modules that depend on it.
    promises.push(downloaderAPI.initialize()
        .then(function LoadAutoDownloadModules() {
            logger.info('Loading NvCommonTasks...');
            NvCommonTasks = require('./NvCommonTasks.js')();
            logger.info('NvCommonTasks loaded');

            logger.info('Loading NvAutoDriverDownload module...');
            NvAutoDriverDownload = require('./NvAutoDriverDownload.js')(NvCommonTasks, NvBackendAPI, downloaderAPI, logger);
            logger.info('NvAutoDriverDownload module loaded');

            logger.info('Loading NvAutoDownload module...');
            NvAutoGFEDownload = require('./NvAutoDownload.js');
            NvAutoGFEDownload.setAppDataPath(GetNvNodeAppdataDirectoryPath());
            logger.info('NvAutoDownload module loaded');
        })
        .then(function InitializeAutoDownloadModules() {
            return NvAutoGFEDownload.initialize(app, io, logger, nvUtil, NvCommonTasks, NvAccountAPI, NvBackendAPI, nvSelfUpdateStatusFilePath);
        }));

    // NvAccountAPI is already loaded, time to initialize it.
    promises.push(NvAccountAPI.initialize());

    try {
        logger.info('Loading NvGameStreamAPI module...');
        NvGameStreamAPI = require('./NvGameStreamAPI.js')(app, io, logger);
        logger.info('NvGameStreamAPI module loaded');
    } catch (err) {
        NvGameStreamAPI = undefined;
        ReportOptionalModuleLoadError(err);
    }

    if (NvGameStreamAPI) {
        promises.push(NvGameStreamAPI.initialize().catch(function (err) {
            NvGameStreamAPI = undefined;
            ReportOptionalModuleLoadError(err);
        }));
    }

    try {
        logger.info('Loading GalleryAPI module...');
        NvGalleryAPI = require('./NvGalleryAPI.js')(app, io, logger);
        logger.info('GalleryAPI module loaded');
    } catch (err) {
        NvGalleryAPI = undefined;
        ReportOptionalModuleLoadError(err);
    }

    let shadowplayDependencies = [];
    if (NvGalleryAPI) {
        let nvGalleryInitPromise = NvGalleryAPI.initialize().catch(function (err) {
            NvGalleryAPI = undefined;
            ReportOptionalModuleLoadError(err);
        });
        shadowplayDependencies.push(nvGalleryInitPromise);
        promises.push(nvGalleryInitPromise);
    }

    try {
        logger.info('Loading NvCameraAPI module...');
        NvCameraAPI = require('./NvCameraAPI.js')(app, io, logger);
        logger.info('NvCameraAPI module loaded');
    } catch (err) {
        NvCameraAPI = undefined;
        ReportOptionalModuleLoadError(err);
    }
    if (NvCameraAPI) {
        let nvCameraInitPromise = NvCameraAPI.initialize().catch(function (err) {
            NvCameraAPI = undefined;
            ReportOptionalModuleLoadError(err);
        });
        shadowplayDependencies.push(nvCameraInitPromise);
        promises.push(nvCameraInitPromise);
    }

    function LoadShadowPlay() {
        try {
            logger.info('Loading ShadowPlayAPI module...');
            ShadowPlayAPI = require('./NvShadowPlayAPI.js')(app, io, logger);
        } catch (err) {
            ShadowPlayAPI = undefined;
            ReportOptionalModuleLoadError(err);
        }
        if (ShadowPlayAPI) {
            logger.info('ShadowPlayAPI initalizing...');
            let spPromise = ShadowPlayAPI.initialize().catch(function (err) {
                logger.error("LoadShadowPlay initialize catch");
                ShadowPlayAPI = undefined;
                ReportOptionalModuleLoadError(err);
            });
            return spPromise;
        }
    }

    function LoadSDK() {
        if (!ShadowPlayAPI) {
            return;
        }

        var api;
        try {
            logger.info('Loading NvSDKAPI...');
            api = require('./shims/NvSDKAPINode.js');
            NvSDKAPI = require('./NvSDKAPI.js')(app, io, logger, api);
            logger.info('NvSDKAPI initalizing...');
            let sdkPromise = NvSDKAPI.initialize().catch(function (err) {
                logger.error("LoadSDK initialize catch");
                NvSDKAPI = undefined;
                ReportOptionalModuleLoadError(err);
            });
            return sdkPromise;
        } catch (err) {
            NvSDKAPI = undefined;
            ReportOptionalModuleLoadError(err);
        }
    }

    promises.push(Promise.all(shadowplayDependencies).then(LoadShadowPlay).then(LoadSDK));

    return Promise.all(promises);
}

////////////////////////////////////////////////////////////////////////////////
// Common endpoints                                                           //
////////////////////////////////////////////////////////////////////////////////

app.get('/version', function (req, res) {
    var data = {};
    data.node = process.version;
    data.NvBackendAPI = NvBackendAPI.version();
    data.NvAccountAPI = NvAccountAPI.version();
    data.DriverInstallAPI = DriverInstallAPI.version();
    data.downloaderAPI = downloaderAPI.version();
    data.NvCommonTasks = NvCommonTasks.version();
    data.NvAutoDriverDownload = NvAutoDriverDownload.version();
    data.NvAutoGFEDownload = NvAutoGFEDownload.version();

    if (NvGameStreamAPI) {
        data.NvGameStreamAPI = NvGameStreamAPI.version();
    }
    if (ShadowPlayAPI) {
        data.ShadowPlayAPI = ShadowPlayAPI.version();
    }
    if (NvGalleryAPI) {
        data.NvGalleryAPI = NvGalleryAPI.version();
    }
    if (NvCameraAPI) {
        data.NvCameraAPI = NvCameraAPI.version();
    }
    if (NvSDKAPI) {
        data.NvSDKAPI = NvSDKAPI.version();
    }

    res.writeHead(200, { 'Content-Type': 'application/json' });

    if (NvAbHubAPI) {
        data.NvAbHubAPI = NvAbHubAPI.version();
    }
    res.end(JSON.stringify(data));
});

app.get('/beta', function (req, res) {

    let flag = false;
    try {
        flag = nvUtil.GetGFE3BetaFlagSync();
    }
    catch (err) {
        flag = false;
    }

    const data = { 'beta': flag };
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify(data));

});

app.get('/Settings/v.1.0/Language', function (req, res) {
    var data = {};
    var languageValue = '';
    try {
        languageValue = nvUtil.GetLanguage();
        logger.info('Language:' + languageValue);
        if (languageValue !== undefined) {
            data.language = languageValue;
        }
        else {
            data.language = '';
        }
        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify(data));
    }
    catch (err) {
        replyWithError(res, err);
    }
});

app.post('/Settings/v.1.0/Language', function (req, res) {
    var parsed = {};
    function onData(data) {
        logger.info('Data:' + data);
        try {
            parsed = JSON.parse(data);
        }
        catch (err) {
            replyWithError(res, err);
        }
    }

    function onEnd() {
        try {
            logger.info('Language:' + parsed.language);
            nvUtil.SaveLanguage(parsed.language);
            setImmediate(function () {
                io.emit('/Settings/v.1.0/Language', { language: parsed.language });
            });
            res.writeHead(200)
            res.end();

            NvBackendAPI.notifyUiLanguageChange(parsed.language);
        }
        catch (err) {
            replyWithError(res, err);
        }
    }

    req.on('data', onData);
    req.on('end', onEnd);
});

app.get('/threadpool', function (req, res) {
    var data = {};
    data.size = process.env.UV_THREADPOOL_SIZE;
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify(data));
});

//empty get, just to check if Node is up.
app.get('/up', function (req, res) {
    res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-cache' });
    res.end();
});

//! Formats the error and makes a reply with appropriate HTTP code.
//! @param res Response object provided by Express.
//! @param err Error object.
function replyWithError(res, err) {
    logger.error(err);
    if ('invalidArgument' in err) {
        res.writeHead(400, { 'Content-Type': 'text/html;charset=utf-8' });
    }
    else {
        res.writeHead(500, { 'Content-Type': 'text/html;charset=utf-8' });
    }

    var errorString = JSON.stringify(err);
    logger.error(errorString);
    res.end(errorString);
}

////////////////////////////////////////////////////////////////////////////////
// Starting up                                                                //
////////////////////////////////////////////////////////////////////////////////

function StartHTTPServer() {

    return new Promise(function StartHTTPServerPromise(resolve, reject) {

        logger.info('Starting HTTP server...');

        // Number of attempts to start listening before giving up.
        let listenAttemptCount = 5;

        function TryListening() {
            let host = '127.0.0.1';
            let portNumber = nvUtil.GetPortOverride();
            if (portNumber) {
                logger.info('Overriding port number with ' + portNumber);
            } else {
                portNumber = 0;
            }
            logger.info('Trying to listen http://%s:%s', host, portNumber);
            httpServer.listen(portNumber, host, OnListening);
            listenAttemptCount--;
        }

        function OnListening() {
            var host = httpServer.address().address;
            var port = httpServer.address().port;

            logger.info('Server is listening at http://%s:%s', host, port);
            //
            // Create file with security current port number and cookie.
            // See also http://jirasw.nvidia.com/browse/CRIMSON-978 and http://jirasw.nvidia.com/browse/CRIMSON-1205
            //
            let config = {};
            config.port = httpServer.address().port;
            config.secret = securityCookie;

            nvUtil.ConfirmInitialization(JSON.stringify(config));
            logger.info('Initialization complete.');
            resolve();
        }

        httpServer.on('error', function HandleHttpServerError(e) {
            logger.error('HTTP server error: ' + e);
            switch (e.syscall) {
                case 'listen':
                    //
                    // Listen failed. Try to listen again if possible.
                    //
                    if (listenAttemptCount > 0) {
                        setTimeout(TryListening, 1000);
                    }
                    else {
                        logger.error('Listen attempt count exceeded, giving up.');
                        reject(e);
                    }
                    break;
                default:
                    //
                    // It is unclear what to do with the rest, just re-throw.
                    //
                    throw e;
            }
        });

        TryListening();
    });
}

////////////////////////////////////////////////////////////////////////////////
// Shutdown logic                                                             //
////////////////////////////////////////////////////////////////////////////////

function Shutdown(exitCode) {
    logger.info('Shutting down.');

    //
    // TODO: call modules shutdown functions here.
    //

    if (NvSDKAPI) {
        NvSDKAPI.Cleanup();
    }

    if (NvCameraAPI) {
        NvCameraAPI.Cleanup();
    }

    if (ShadowPlayAPI) {
        ShadowPlayAPI.Cleanup();
    }

    if (NvAbHubAPI) {
        NvAbHubAPI.cleanup();
    }

    if (downloaderAPI) {
        downloaderAPI.cleanup();
    }

    if (NvAccountAPI) {
        NvAccountAPI.cleanup();
    }

    if (NvGameStreamAPI) {
        NvGameStreamAPI.cleanup();
    }

    if (NvPiplConfig) {
        NvPiplConfig.cleanup();
    }

    logger.infoSync('Stopping logging.');
    logger.destroyLogger();
    process.exit(exitCode);
};

function OnSIGTERM() {
    logger.info('Received SIGTERM.');
    Shutdown();
}

function OnSIGINT() {
    logger.info('Received SIGINT.');
    Shutdown();
}

function OnShutdownRequested() {
    logger.info('Shutdown requested.');
    Shutdown();
}

function RegisterShutdownCallbacks() {
    logger.debug('Registering shutdown callbacks...');
    process.on('SIGTERM', OnSIGTERM);
    process.on('SIGINT', OnSIGINT);
    nvUtil.SetExitCallback(OnShutdownRequested);
}
