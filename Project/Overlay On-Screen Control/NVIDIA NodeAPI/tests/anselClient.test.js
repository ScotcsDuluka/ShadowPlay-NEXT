'use strict';
const assert = require('node:assert/strict');
const { test } = require('node:test');
const { EventEmitter } = require('node:events');
const { AnselClient } = require('../shims/ansel-client');

function clientFor(reply, fragmentSize = 16384) {
  const wire = JSON.stringify(reply) + '\n';
  return new AnselClient({
    list: async () => ['NVIDIAAnsel.123.1'],
    connect: () => {
      const socket = new EventEmitter();
      let destroyed = false;
      socket.setEncoding = () => {};
      socket.destroy = () => { destroyed = true; };
      socket.write = () => queueMicrotask(() => {
        for (let pos = 0; pos < wire.length && !destroyed; pos += fragmentSize)
          socket.emit('data', wire.slice(pos, pos + fragmentSize));
      });
      queueMicrotask(() => socket.emit('connect'));
      return socket;
    }
  });
}

test('receives a fragmented catalog larger than the old 1 MiB limit', async () => {
  const filters = Array.from({length: 2400}, (_, i) => ({
    id: 'filter-' + i, name: 'Effect ' + i,
    controls: [{id:'Strength', label:'ค่า '.repeat(160), currentValue:[0.5]}]
  }));
  const state = {protocol:1, pid:123, session:'test', generation:1, filters,
    stack:['filter-1'], enabled:true};
  assert(Buffer.byteLength(JSON.stringify(state)) > 1024 * 1024);
  const result = await clientFor({ok:true, state}).state();
  assert.deepEqual(result.state.filters, filters);
});

test('keeps a finite response bound', async () => {
  await assert.rejects(clientFor({padding:'x'.repeat(16 * 1024 * 1024)}).state(),
    error => error.code === 'ANSEL_INVALID_REPLY');
});

test('discovery preserves the actual catalog failure', async () => {
  await assert.rejects(clientFor({ok:false, status:413, code:'CATALOG_TOO_LARGE',
    message:'Catalog exceeds limit'}).state(),
  error => error.code === 'CATALOG_TOO_LARGE' && error.status === 413);
});
