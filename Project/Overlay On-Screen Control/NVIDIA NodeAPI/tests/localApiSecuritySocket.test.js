'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const express = require('express');
const socketIO = require('socket.io');
const security = require('../localApiSecurity');

test('actual Engine.IO handshake blocks external origins before Socket.IO connects', async () => {
    const app = express();
    app.use(security.httpMiddleware);
    app.get('/up', (_req, res) => res.status(200).end());
    const server = http.createServer(app);
    // Short heartbeat prevents test-only polling sessions retaining default 25s timers.
    const io = socketIO(server, { pingInterval: 200, pingTimeout: 200 });
    io.eio.allowRequest = security.socketAllowRequest;
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));

    const port = server.address().port;
    function poll(headers) {
        return new Promise((resolve, reject) => {
            const req = http.get({
                hostname: '127.0.0.1', port,
                path: '/socket.io/?EIO=3&transport=polling',
                headers: headers || {}
            }, res => {
                const chunks = [];
                res.on('data', d => chunks.push(d));
                res.on('end', () => resolve({ code: res.statusCode, body: Buffer.concat(chunks).toString('utf8') }));
            });
            req.on('error', reject);
        });
    }

    try {
        const outsider = await poll({ Origin: 'https://attacker.example' });
        // Engine.IO maps a denied handshake to HTTP 400 (not Express 403).
        assert.equal(outsider.code, 400);
        // Positive native/OSC routes are exercised in localApiSecurity.test.js.
        // No open polling sessions here, so teardown cannot strand heartbeats.
        const hostileHost = await poll({ Host: 'attacker.example:' + port });
        assert.equal(hostileHost.code, 400);
    } finally {
        // Polling handshakes allocate live Engine.IO sessions: close each one
        // before shutting down the HTTP server so the test leaves no sockets.
        Object.keys(io.eio.clients).forEach(id => io.eio.clients[id].close(true));
        await new Promise(resolve => io.close(resolve));
    }
});
