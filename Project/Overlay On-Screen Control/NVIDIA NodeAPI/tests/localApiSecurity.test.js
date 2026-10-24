'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const express = require('express');
const security = require('../localApiSecurity.js');

function request(headers, method, url, remoteAddress) {
    return {
        headers: Object.assign({ host: '127.0.0.1:59011' }, headers || {}),
        method: method || 'POST',
        url: url || '/ShadowPlay/v.1.0/Record/Enable',
        socket: { remoteAddress: remoteAddress || '127.0.0.1' }
    };
}

test('allows OSC browser on either exact loopback origin and native Container callers', () => {
    assert.equal(security.authorize(request({
        origin: 'http://127.0.0.1:59011',
        referer: 'http://127.0.0.1:59011/osc/index.html',
        'sec-fetch-site': 'same-origin'
    })), true);
    assert.equal(security.authorize(request({
        host: 'localhost:59011',
        origin: 'http://localhost:59011',
        referer: 'http://localhost:59011/osc/',
        'sec-fetch-site': 'same-origin'
    })), true);
    assert.equal(security.authorize(request({}, 'POST', '/Backend/v.1.0/ContainerEvents')), true);
    assert.equal(security.authorize(request({}, 'POST', '/?hk=OpenShare', '::ffff:127.0.0.1')), true);
});

test('blocks cross-site origins, opaque origins, malicious referrers and hostile hosts', () => {
    assert.equal(security.authorize(request({ origin: 'https://attacker.example' })), false);
    assert.equal(security.authorize(request({ origin: 'null' })), false);
    assert.equal(security.authorize(request({ origin: 'http://127.0.0.1:9090' })), false);
    assert.equal(security.authorize(request({ referer: 'https://attacker.example/page' })), false);
    assert.equal(security.authorize(request({ referer: 'http://127.0.0.1:9090/page' })), false);
    assert.equal(security.authorize(request({ 'sec-fetch-site': 'cross-site' })), false);
    assert.equal(security.authorize(request({ host: 'attacker.example:59011' })), false);
    assert.equal(security.authorize(request({}, 'POST', '/', '192.0.2.1')), false);
});

test('Socket.IO Engine.IO handshake uses same request guard', () => {
    let permitted;
    security.socketAllowRequest(request({ origin: 'http://localhost:59011', host: 'localhost:59011' }), (err, value) => { assert.equal(err, null); permitted = value; });
    assert.equal(permitted, true);
    security.socketAllowRequest(request({ origin: 'https://attacker.example' }), (err, value) => { assert.equal(err, null); permitted = value; });
    assert.equal(permitted, false);
});

test('HTTP boundary applies before routes and leaves local health/read-only traffic intact', async () => {
    const app = express();
    app.use(security.httpMiddleware);
    app.get('/Backend/v.1.0/health', (_req, res) => res.json({ ok: true }));
    app.post('/cefquery', (_req, res) => res.json({ accepted: true }));
    app.post('/Backend/v.1.0/ContainerEvents', (_req, res) => res.json({ accepted: true }));
    const server = app.listen(0, '127.0.0.1');
    await new Promise(resolve => server.once('listening', resolve));
    const port = server.address().port;
    async function call(path, method, headers) {
        return new Promise((resolve, reject) => {
            const request = http.request({ hostname: '127.0.0.1', port, path, method, headers }, res => {
                let body = '';
                res.on('data', chunk => { body += chunk; });
                res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body }));
            });
            request.on('error', reject);
            request.end();
        });
    }
    try {
        assert.equal((await call('/Backend/v.1.0/health', 'GET')).status, 200);
        assert.equal((await call('/cefquery', 'POST', { origin: 'http://127.0.0.1:' + port })).status, 200);
        assert.equal((await call('/Backend/v.1.0/ContainerEvents', 'POST')).status, 200);
        const denied = await call('/cefquery', 'POST', { origin: 'https://attacker.example' });
        assert.equal(denied.status, 403);
        assert.equal(denied.headers['access-control-allow-origin'], undefined);
        const hotkey = await call('/?hk=OpenShare', 'GET');
        assert.equal(hotkey.status, 405);
        assert.equal((await call('/?hk=OpenShare', 'POST')).status, 404);
        assert.equal((await call('/Backend/v.1.0/health', 'GET', { host: 'attacker.example' })).status, 403);
    } finally {
        await new Promise(resolve => server.close(resolve));
    }
});
