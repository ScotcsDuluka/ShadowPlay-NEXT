'use strict';
const assert = require('assert');
const http = require('http');
const path = require('path');
const fs = require('fs');
const settingsPath = path.join(require('os').tmpdir(), 'duluka-filter-settings-' + process.pid + '.json');
const { spawn } = require('child_process');
const express = require('express');
const mount = require('../shims/game-filter-route');
const { AnselClient } = require('../shims/ansel-client');

async function main() {
  const executable = path.resolve(__dirname, '../../../NVIDIA Adapter/Ansel Adapter/tests/bin/x64/Release/Ansel Session Tests.exe');
  const child = spawn(executable, ['--serve'], { windowsHide: true, stdio: ['ignore','pipe','pipe'] });
  let server, route;
  try {
    const pid = await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('Native test pipe did not start')), 5000);
      child.once('error', reject);
      child.once('exit', code => reject(new Error('Native test process exited: ' + code)));
      child.stdout.on('data', data => {
        const match = String(data).match(/READY (\d+)/);
        if (match) { clearTimeout(timer); resolve(Number(match[1])); }
      });
    });
    const client = new AnselClient({ list: async () => ['NVIDIAAnsel.' + pid + '.1'] });
    let exchanges = 0;
    const exchange = client.exchange.bind(client);
    client.exchange = (...args) => { exchanges++; return exchange(...args); };
    const discovered = await new AnselClient().state(pid);
    assert.strictEqual(discovered.state.pid, pid, 'Windows named-pipe runtime discovery failed');
    const events = [];
    const app = express();
    route = mount(app, { emit: (channel, data) => events.push({ channel, data }) }, { client, poll: false, settingsPath });
    app.use((req, res) => res.status(404).json({ unhandled: true }));
    server = await new Promise(resolve => { const instance = app.listen(0, '127.0.0.1', () => resolve(instance)); });
    function call(method, endpoint, body, headers) {
      return new Promise((resolve, reject) => {
        const payload = body === undefined ? undefined : JSON.stringify(body);
        const req = http.request({ hostname:'127.0.0.1', port:server.address().port, method,
          path:endpoint, headers:Object.assign(payload ? {'Content-Type':'application/json','Content-Length':Buffer.byteLength(payload)} : {}, headers || {}) }, res => {
          let raw = ''; res.on('data', c => raw += c); res.on('end', () => resolve({status:res.statusCode, body:JSON.parse(raw)}));
        });
        req.on('error', reject); req.end(payload);
      });
    }
    const base = '/NvCamera/v.1.0';
    let focusResult = await call('POST', '/Duluka/v.1.0/GameFilter/Focus', {pid:123,executable:'explorer',title:'Desktop'});
    assert.strictEqual(focusResult.body.message,'Desktop: explorer');
    assert(events.some(e => e.channel === '/Duluka/v1/OverlayGameEntry' && e.data.pid === 123));
    focusResult = await call('POST', '/Duluka/v.1.0/GameFilter/Focus', {pid:'bad'});
    assert.strictEqual(focusResult.status,400);
    let result = await call('POST', base + '/GetAvailable', {});
    assert.strictEqual(result.body.available, true);
    assert(events.some(e => e.channel === base + '/Notifications' && e.data.type === 'available' && e.data.features.includes('mods')));
    const beforeProbes = exchanges;
    await Promise.all(Array.from({length: 12}, () => call('GET', '/Duluka/v.1.0/GameFilter/State')));
    await route.poll();
    assert.strictEqual(exchanges, beforeProbes, 'availability probes or idle polling enumerated the native catalog');
    await call('GET', '/Duluka/v.1.0/GameFilter/State?refresh=1');
    assert.strictEqual(exchanges, beforeProbes + 1, 'explicit refresh must read the live runtime');
    const configUrl = '/Duluka/v.1.0/GameFilter/Settings';
    const enabledConfig = {mode:'reshade',allowConnection:true,connectionManager:true};
    for (const config of [Object.assign({},enabledConfig,{allowConnection:false}),Object.assign({},enabledConfig,{connectionManager:false}),Object.assign({},enabledConfig,{mode:'nvidia'})]) {
      result = await call('POST', configUrl, config);
      assert.strictEqual(result.body.bridgeEnabled,false);
      const beforeDisabled = exchanges;
      result = await call('GET', '/Duluka/v.1.0/GameFilter/State');
      assert.strictEqual(result.body.code,'BRIDGE_DISABLED');
      result = await call('POST',base+'/Filter',{type:'test/filter-a',stackIdx:0});
      assert.strictEqual(result.status,503);
      await route.poll();
      assert.strictEqual(exchanges,beforeDisabled,'disabled connection still touched the native runtime');
      assert.deepStrictEqual(JSON.parse(fs.readFileSync(settingsPath,'utf8')),config);
    }
    result = await call('POST',configUrl,{mode:'invalid',allowConnection:true,connectionManager:true});
    assert.strictEqual(result.status,400);
    await call('POST',configUrl,enabledConfig);
    result = await call('POST', base + '/GetFilters', {});
    assert.deepStrictEqual(result.body.filters.map(f => f.id), ['test/filter-a','test/filter-b']);
    assert(events.some(e => e.data.type === 'filters' && e.data.filters.length === 2));
    result = await call('POST', base + '/Filter', { type:'test/filter-a', stackIdx:0 });
    assert.strictEqual(result.status, 200);
    result = await call('POST', '/NvCamera/v.1.1/Filter/' + encodeURIComponent('test/filter-a') + '/Attribute',
      {controlId:'Strength',value:[0.8],stackIdx:0});
    assert.strictEqual(result.status, 200);
    result = await call('POST', base + '/Filter/GetInfo', {stackIdx:0});
    assert.strictEqual(result.body.controls[0].currentValue[0], 0.8000000000000000);
    const beforePanelReads = exchanges;
    await Promise.all(Array.from({length: 6}, () => call('POST', base + '/Filter/GetInfo', {stackIdx:0})));
    await call('POST', base + '/GetFilters', {});
    assert.strictEqual(exchanges, beforePanelReads, 'panel reads queued redundant native snapshots after a verified write');
    assert(events.some(e => e.data.type === 'currentFilter' && e.data.controls[0] && e.data.controls[0].currentValue[0] === 0.8));
    result = await call('POST', base + '/Filter/' + encodeURIComponent('test/filter-a') + '/Attribute',
      {controlId:'Strength',value:[2],stackIdx:0});
    assert.strictEqual(result.status, 400);
    assert.strictEqual(result.body.codeText, 'OUT_OF_RANGE');
    result = await call('POST', base + '/Filter/GetInfo', {stackIdx:0});
    assert.strictEqual(result.body.controls[0].currentValue[0], 0.8, 'rejected write changed effect value');
    result = await call('POST', base + '/EnableMods', {globalEnable:false});
    assert.strictEqual(result.status, 200);
    result = await call('GET', '/Duluka/v.1.0/GameFilter/State');
    assert.strictEqual(result.body.filters[0].enabled, false);
    result = await call('POST', base + '/EnableMods', {globalEnable:true});
    assert.strictEqual(result.status, 200);
    result = await call('POST', base + '/Capture/Control', {capture:true});
    assert.strictEqual(result.status, 200);
    assert(events.some(e => e.data.type === 'captureControlChange' && e.data.capture === 1));
    result = await call('GET', base + '/GetProcessInfo');
    assert.strictEqual(result.body.procId, pid);
    assert.strictEqual(result.body.procPath, 'test-only-fake-runtime.exe');
    result = await call('GET', base + '/ReshadeSupported'); assert.strictEqual(result.body.reshadeSupported, true);
    result = await call('GET', base + '/SetMultipleFilterAPISupport'); assert.strictEqual(result.body.isSetMultipleFilterAPISupported, true);
    result = await call('POST', base + '/Filter/setMultipleFiltersAndAttributes', {filters:[
      {id:'test/filter-b',stackIdx:0,controls:[]},
      {id:'test/filter-a',stackIdx:1,controls:[{id:'Strength',value:[0.3]}]}
    ]});
    assert.strictEqual(result.status, 200);
    result = await call('GET', '/Duluka/v.1.0/GameFilter/State');
    assert.deepStrictEqual(result.body.stack, ['test/filter-b','test/filter-a']);
    assert(events.some(e => e.data.type === 'multipleSetFilterAndAttributes' && e.data.status === 1), 'OSC preset completion notification is missing');
    assert.strictEqual(result.body.filters[0].controls[0].currentValue[0], 0.3);
    const staleTarget = await client.state();
    await client.exchange(staleTarget.name, {op:'testReload'});
    await assert.rejects(client.command(staleTarget,'clear',{}), error => error.status === 409 && error.code === 'STALE_SESSION');
    await assert.rejects(client.exchange(staleTarget.name, {op:'clear',deadline:1}), error => error.code === 'EXPIRED_COMMAND');
    result = await call('POST', base + '/UnknownAction', {}); assert.strictEqual(result.status, 501);
    result = await call('GET', base + '/Filter'); assert.strictEqual(result.status, 405);
    result = await call('GET', base + '/GetFilters', undefined, {'x-ansel-game-pid':'invalid'}); assert.strictEqual(result.status, 400);
    result = await call('POST', base + '/GetFilters', {padding:'x'.repeat(66000)}); assert.strictEqual(result.status, 413);
    const exit = new Promise(resolve => child.once('exit', resolve)); child.kill(); await exit;
    result = await call('POST', base + '/GetAvailable', {}); assert.strictEqual(result.body.available, false);
    assert(events.some(e => e.data.type === 'anselState' && e.data.connected === false));
    result = await call('POST', base + '/Filter', {type:'test/filter-a',stackIdx:0}); assert.strictEqual(result.status, 503);
    result = await call('GET', base + '/ReshadeSupported'); assert.strictEqual(result.body.reshadeSupported, false);
    result = await call('POST', base + '/EnableMods', {globalEnable:false});
    assert.strictEqual(result.body.applied, false, 'preference save must not claim live application without a game');
    console.log('PASS: OSC HTTP + real native named pipe + explicitly fake effects: catalog, slider/readback, events, toggles, presets, reload, expiry, errors and disconnect');
  } finally {
    if (fs.existsSync(settingsPath)) fs.unlinkSync(settingsPath);
    if (route) route.close();
    if (server) await new Promise(resolve => server.close(resolve));
    if (!child.killed) child.kill();
  }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
