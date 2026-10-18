'use strict'
// routes/families.js — the missing node-bridge route families proven by
// docs/OSC-NODE-API-EXTRACTION.md §B (ZCode mission 2), implemented with the
// production methods, paths, status codes and body semantics:
//
//   QuietMode2/v.1.0   GET/POST /state, GET /support      (NvBackendAPI.js:735-797)
//   Nis2/v.1.0         GET /:cmsId/state, POST /state     (843-896; cmsId "0" = global)
//   DeepDVC/v.1.0      GET /:cmsId/state, POST /state     (898-949)
//   Feedback/v.0.1     POST /            → 202 empty      (1471-1491)
//   abHubAPI/v.0.1     POST /Post|/Add|/Delete → 200 EMPTY body;
//                      GET /Status → 200 EMPTY (the 503 arg only applies on
//                      the error path — NvAbHubAPI.js:144-148);
//                      /abHubAPI/v.0.1/Status socket emit `true` after init
//                      (NvAbHubAPI.js:168-172)
//   gfeupdate          GET /autoGFEDownload/:key → raw text body;
//                      POST :key → update + emits 'GFEbetaValue' and
//                      emit(req.url, rawText)  (NvAutoDownload.js:840-858)
//   GameShare/v.1.0    POST /CreateSession → 202 empty + socket
//                      /GameShare/v.1.0/CreateSession; PUT
//                      /ConfigureControllerMapping {mode}; PUT
//                      /ModifySession/*; DELETE /Session/*; GET
//                      /FullScreenProcessId/*  (NvGameShareAPI.js:63-305)
//
// Settings state persists in our store layer (production delegates to native
// modules — the wire contract is what we mirror).

module.exports = function familyRoutes(app, ctx) {
    const store = ctx.store;
    const socket = ctx.socket;

    function stateReply(res, data) {
        res.status(200).json(data);
    }
    function emptyReply(res) {
        // production mutation success: 200 with an EMPTY body (res.end())
        res.status(200).end();
    }

    // ── QuietMode2 ──────────────────────────────────────────────────
    app.get('/QuietMode2/v.1.0/state', function (req, res) {
        stateReply(res, store.getSection('quietMode2State'));
    });
    app.post('/QuietMode2/v.1.0/state', function (req, res) {
        if (req.body && typeof req.body === 'object') store.storeSection('quietMode2State', req.body);
        emptyReply(res);
    });
    app.get('/QuietMode2/v.1.0/support', function (req, res) {
        stateReply(res, store.getSection('quietMode2Support'));
    });

    // ── Nis2 (image sharpening) ─────────────────────────────────────
    app.get('/Nis2/v.1.0/:cmsId/state', function (req, res) {
        stateReply(res, store.getSection('nis2:' + req.params.cmsId));
    });
    app.post('/Nis2/v.1.0/state', function (req, res) {
        if (req.body && typeof req.body === 'object') {
            const cmsId = String(req.body.cmsId !== undefined ? req.body.cmsId : '0');
            store.storeSection('nis2:' + cmsId, req.body);
        }
        emptyReply(res);
    });

    // ── DeepDVC (vibrance) ──────────────────────────────────────────
    app.get('/DeepDVC/v.1.0/:cmsId/state', function (req, res) {
        stateReply(res, store.getSection('deepdvc:' + req.params.cmsId));
    });
    app.post('/DeepDVC/v.1.0/state', function (req, res) {
        if (req.body && typeof req.body === 'object') {
            const cmsId = String(req.body.cmsId !== undefined ? req.body.cmsId : '0');
            store.storeSection('deepdvc:' + cmsId, req.body);
        }
        emptyReply(res);
    });

    // ── Feedback ────────────────────────────────────────────────────
    // trailing-slash route in production (NvBackendAPI.js:1471); accept the
    // bare path too (Express does not strip it automatically)
    app.post('/Feedback/v.0.1', function (req, res) { addFeedback(req, res); });
    app.post('/Feedback/v.0.1/', function (req, res) { addFeedback(req, res); });

    function addFeedback(req, res) {
        const body = (req.body && typeof req.body === 'object') ? req.body : {};
        const log = store.getSection('feedback');
        const items = Array.isArray(log.items) ? log.items : [];
        items.push({ at: new Date().toISOString(), body: body });
        store.storeSection('feedback', { items: items.slice(-20) });
        // production: AddFeedback answers 202 with an empty body
        res.status(202).end();
    }

    // ── abHub (A/B experiments) ─────────────────────────────────────
    function abPost(req, res) {
        // stringifyVariantData coerces experiments[].variant.data to a
        // string before the native call; the reply is 200 with an EMPTY
        // body (NvAbHubAPI.js:113-148)
        const content = (req.body && typeof req.body === 'object') ? req.body : {};
        if (Array.isArray(content.experiments)) {
            for (const item of content.experiments) {
                if (item && item.variant && typeof item.variant.data !== 'string') {
                    item.variant.data = JSON.stringify(item.variant.data);
                }
            }
        }
        const log = store.getSection('abHubMessages');
        const items = Array.isArray(log.items) ? log.items : [];
        items.push({ at: new Date().toISOString(), body: content });
        store.storeSection('abHubMessages', { items: items.slice(-20) });
        emptyReply(res);
    }
    app.post('/abHubAPI/v.0.1/Post', abPost);
    app.post('/abHubAPI/v.0.1/Add', abPost);
    app.post('/abHubAPI/v.0.1/Delete', abPost);
    app.get('/abHubAPI/v.0.1/Status', function (req, res) {
        // production quirk verbatim: doReply(res, false, '/Status', 503)
        // answers 200 with an empty body — the 503 only applies on the
        // error path (NvAbHubAPI.js:144-148)
        emptyReply(res);
    });
    // production emits `true` on this channel once the module initialized
    // (NvAbHubAPI.js:168-172) — mirror it once at registration time
    setImmediate(function () {
        socket.emitChannel('/abHubAPI/v.0.1/Status', true);
    });

    // ── gfeupdate (auto-GFE / experimental flag) ────────────────────
    // GET answers the RAW stored text (no JSON envelope); POST updates the
    // store, emits 'GFEbetaValue' (the autoGFEbeta value) and emits the
    // REQUEST URL as the event name with the raw text as the payload —
    // the page's /gfeupdate/autoGFEDownload/autoGFEbeta listener
    // (NvAutoDownload.js:840-858)
    function gfeGet(req, res) {
        const key = String(req.params.key || '');
        const sec = store.getSection('gfeupdate');
        res.status(200).send(String(sec[key] !== undefined ? sec[key] : '0'));
    }
    function gfePost(req, res) {
        const key = String(req.params.key || '');
        const text = (req.rawBody !== undefined && req.rawBody !== null) ? String(req.rawBody) : '';
        store.storeSection('gfeupdate', (function () {
            const s = {};
            s[key] = text;
            return s;
        })());
        const sec = store.getSection('gfeupdate');
        socket.emitChannel('GFEbetaValue', String(sec.autoGFEbeta !== undefined ? sec.autoGFEbeta : '0'));
        socket.emitChannel(req.originalUrl.split('?')[0], text);
        res.status(200).end();
    }
    app.get('/gfeupdate/autoGFEDownload/:key', gfeGet);
    app.post('/gfeupdate/autoGFEDownload/:key', gfePost);

    // ── GameShare (CoPlay sessions) ─────────────────────────────────
    app.get('/GameShare/v.1.0/FullScreenProcessId/*', function (req, res) {
        // production returns the native fullscreen pid for the active window;
        // standalone floor: no session → pid 0 (NvGameShareAPI.js:63-85)
        const activeWindow = req.params[0];
        if (!activeWindow) {
            res.status(400).send('Error: No active Window provided in the request.');
            return;
        }
        stateReply(res, { processId: 0 });
    });
    app.put('/GameShare/v.1.0/ConfigureControllerMapping', function (req, res) {
        if (req.body && typeof req.body === 'object') {
            store.storeSection('gameShareControllerMapping', req.body);
        }
        emptyReply(res);
    });
    app.post('/GameShare/v.1.0/CreateSession', function (req, res) {
        const body = (req.body && typeof req.body === 'object') ? req.body : {};
        const session = {
            sessionId: 'nvsp-' + Date.now(),
            activeWindow: body.activeWindow || '',
            displayName: body.displayName || '',
            inviteMode: body.inviteMode || ''
        };
        store.storeSection('gameShareSession', session);
        // production: the native CreateSession callback emits the result
        // (NvGameShareAPI.js:189-282); standalone emits the session we created
        socket.emitChannel('/GameShare/v.1.0/CreateSession', session);
        // production: CreateSession answers 202 with an empty body
        res.status(202).end();
    });
    app.put('/GameShare/v.1.0/ModifySession/*', function (req, res) {
        const sessionId = req.params[0];
        if (!sessionId) {
            res.status(400).send('Error: No session id in the request.');
            return;
        }
        const body = (req.body && typeof req.body === 'object') ? req.body : {};
        store.storeSection('gameShareSession', Object.assign({ sessionId: sessionId }, body));
        emptyReply(res);
    });
    app.delete('/GameShare/v.1.0/Session/*', function (req, res) {
        const sessionId = req.params[0];
        if (!sessionId) {
            res.status(400).send('Error: No session id in the request.');
            return;
        }
        store.clearSection('gameShareSession');
        emptyReply(res);
    });
};
