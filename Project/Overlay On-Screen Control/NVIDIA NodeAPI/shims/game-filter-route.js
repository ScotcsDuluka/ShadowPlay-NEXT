'use strict';
const { AnselClient, failure } = require('./ansel-client');
const CHANNEL = '/NvCamera/v.1.0/Notifications';
function gameWatcherGames() {
  return new Promise(resolve => {
    let raw = '', done = false;
    const finish = games => { if (!done) { done = true; resolve(games); } };
    const request = require('http').get('http://127.0.0.1:59012/v1/game-watcher/status', response => {
      response.on('data', chunk => { raw += chunk; if (raw.length > 1048576) { finish([]); request.destroy(); } });
      response.on('end', () => { try { const value = JSON.parse(raw); finish(Array.isArray(value.matchedGames) ? value.matchedGames : []); } catch (_) { finish([]); } });
      response.on('error', () => finish([]));
    });
    request.setTimeout(500, () => { finish([]); request.destroy(); });
    request.on('error', () => finish([]));
  });
}

function readBody(req) {
  if (req.body && typeof req.body === 'object') return Promise.resolve(req.body);
  return new Promise((resolve, reject) => {
    let raw = '', tooLarge = false;
    req.on('error', reject);
    req.on('data', data => {
      if (tooLarge) return;
      raw += data;
      if (Buffer.byteLength(raw) > 65536) { tooLarge = true; reject(failure(413, 'BODY_TOO_LARGE', 'Filter request exceeds 64 KiB')); }
    });
    req.on('end', () => {
      if (tooLarge) return;
      try {
        const body = raw ? JSON.parse(raw) : {};
        if (!body || typeof body !== 'object' || Array.isArray(body)) throw new Error();
        resolve(body);
      } catch (_) { reject(failure(400, 'INVALID_JSON', 'A JSON object is required')); }
    });
  });
}

function mount(app, io, options) {
  options = options || {};
  const fs = require('fs');
  const settingsPath = options.settingsPath || require('path').join(__dirname, '..', 'game-filter-settings.json');
  const defaults = {mode:'reshade',allowConnection:true,connectionManager:true};
  function validSettings(value) {
    return value && ['reshade','duluka','nvidia'].includes(value.mode) &&
      typeof value.allowConnection === 'boolean' && typeof value.connectionManager === 'boolean';
  }
  let configuration = Object.assign({}, defaults);
  let configurationVersion = 0;
  try { const saved = JSON.parse(fs.readFileSync(settingsPath, 'utf8')); if (validSettings(saved)) configuration = {mode:saved.mode,allowConnection:saved.allowConnection,connectionManager:saved.connectionManager}; }
  catch (error) { if (error.code !== 'ENOENT') console.error('[game-filter] settings load failed:', error.message); }
  function bridgeEnabled() { return configuration.mode === 'reshade' && configuration.allowConnection && configuration.connectionManager; }
  function requireBridge(version) {
    if (!bridgeEnabled()) throw failure(503, 'BRIDGE_DISABLED', 'ReShade connection is disabled in Game Filter settings');
    if (version !== configurationVersion) throw failure(409, 'BRIDGE_CHANGED', 'Game Filter settings changed. Reopen the editor to continue.');
  }
  const client = options.client || new AnselClient();
  let last = null, lastCatalog = '', stopped = false, polling = false, pendingEnable, editingTarget = null, needsSession = false;
  let cachedTarget = null, stateRead = null, cachedAt = 0, presetSaveTimer = null;
  function cancelPresetSave() {
    if (presetSaveTimer) clearTimeout(presetSaveTimer);
    presetSaveTimer = null;
  }
  function schedulePresetSave(target, version) {
    cancelPresetSave();
    if (!target.state.preset || !(target.state.preset.activeSlot >= 1 && target.state.preset.activeSlot <= 3)) return;
    // Coalesce slider drags. ReShade writes the INI, never the Node bridge.
    presetSaveTimer = setTimeout(async () => {
      presetSaveTimer = null;
      if (!bridgeEnabled() || version !== configurationVersion ||
          !cachedTarget || cachedTarget.name !== target.name ||
          cachedTarget.state.session !== target.state.session ||
          cachedTarget.state.preset.activeSlot !== target.state.preset.activeSlot) return;
      try {
        const updated = await client.command(cachedTarget, 'presetSave', {}, () => requireBridge(version));
        requireBridge(version);
        cachedTarget = {name:target.name,state:updated};
        if (editingTarget && editingTarget.name === target.name)
          editingTarget = cachedTarget;
      } catch (error) {
        console.error('[game-filter] ReShade preset autosave failed:', error.message);
      }
    }, 750);
    if (presetSaveTimer.unref) presetSaveTimer.unref();
  }
  // Availability probes must not enumerate thousands of uniforms on the game
  // render thread. Interaction reads and verified command replies refresh this.
  async function cachedState(pid, refresh, version) {
    requireBridge(version);
    if (!refresh && cachedTarget && (!pid || cachedTarget.state.pid === pid)) {
      const names = await client.list();
      requireBridge(version);
      const eligible = names.filter(name => /^NVIDIAAnsel\.\d+\.\d+$/.test(name) && (!pid || name.startsWith('NVIDIAAnsel.' + pid + '.')));
      if (eligible.length === 1 && eligible[0] === cachedTarget.name) return cachedTarget;
      cachedTarget = null;
    }
    if (stateRead && stateRead.pid === pid && stateRead.version === version) return stateRead.promise;
    const flight = { pid, version };
    flight.promise = client.state(pid, () => requireBridge(version)).then(target => {
      requireBridge(version);
      cachedTarget = target;
      cachedAt = Date.now();
      return target;
    }).finally(() => { if (stateRead === flight) stateRead = null; });
    stateRead = flight;
    return flight.promise;
  }
  function emit(event) { if (io) io.emit(CHANNEL, event); }
  function currentFilter(state, slot, type) {
    const id = state.stack[slot];
    const filter = state.filters.find(item => item.id === id);
    return { type: type || 'currentFilter', status: 1, stackIdx: slot,
      id: id || 'None', filterId: id || 'None', controls: filter ? filter.controls : [] };
  }
  function publish(state, suppressCurrent) {
    const identity = state.session + ':' + state.generation;
    if (!last || last.session + ':' + last.generation !== identity) {
      if (editingTarget && (editingTarget.state.session !== state.session || editingTarget.state.generation !== state.generation)) {
        editingTarget = null; needsSession = true;
      }
      emit({ type: 'nvCameraReady', deviceCreationCounter: state.generation });
      emit({ type: 'anselState', connected: true, processId: state.pid, session: state.session, generation: state.generation });
    }
    const catalog = JSON.stringify(state.filters.map(f => ({ id: f.id, name: f.name })));
    if (catalog !== lastCatalog || !last || last.generation !== state.generation) {
      emit({ type: 'filters', filters: state.filters.map(f => ({ id: f.id, name: f.name })) });
      lastCatalog = catalog;
    }
    if (!suppressCurrent) {
      // Include slots removed directly in ReShade; otherwise OSC retains the
      // previous filter pane when the native stack becomes shorter.
      const length = Math.max(state.stack.length, last ? last.stack.length : 0);
      for (let index = 0; index < length; index++) {
        const before = last && currentFilter(last, index), after = currentFilter(state, index);
        if (JSON.stringify(before) !== JSON.stringify(after)) emit(after);
      }
    }
    last = state;
  }
  function disconnected(error) {
    if (last) {
      emit({ type: 'anselState', connected: false, processId: last.pid, reason: error.code });
      emit({ type: 'available', features: [] });
      emit({ type: 'filters', filters: [] });
    }
    if (editingTarget) needsSession = true;
    cancelPresetSave();
    editingTarget = null; last = null; lastCatalog = ''; cachedTarget = null;
  }
  async function poll() {
    if (stopped || polling) return;
    if (!bridgeEnabled()) return;
    polling = true;
    const version = configurationVersion;
    try {
      // Only inspect pipe names while idle; a full snapshot is expensive even
      // when the panel is closed. Pending preferences need one real command.
      const names = await client.list();
      requireBridge(version);
      if (cachedTarget && !names.includes(cachedTarget.name)) disconnected(failure(503, 'ANSEL_UNAVAILABLE', 'Game runtime disconnected'));
      if (pendingEnable === undefined || !names.some(name => /^NVIDIAAnsel\.\d+\.\d+$/.test(name))) return;
      const target = await client.state(undefined, () => requireBridge(version));
      requireBridge(version);
      let state = target.state;
      if (pendingEnable !== undefined) {
        const enabled = pendingEnable;
        state = await client.command(target, 'mods', { globalEnable: enabled }, () => requireBridge(version));
        requireBridge(version);
        if (pendingEnable === enabled) pendingEnable = undefined;
      }
      publish(state);
      cachedTarget = { name: target.name, state };
      if (editingTarget) editingTarget = { name: target.name, state };
    }
    catch (error) { if (version === configurationVersion) disconnected(error); }
    finally { polling = false; }
  }
  const timer = options.poll === false ? null : setInterval(poll, 1000);
  if (timer && timer.unref) timer.unref();

  app.use(async function gameFilter(req, res, next) {
    const match = req.path.match(/^\/NvCamera\/v\.1\.[01](\/.*)?$/);
    const focus = req.path === '/Duluka/v.1.0/GameFilter/Focus';
    const settingsRoute = req.path === '/Duluka/v.1.0/GameFilter/Settings';
    const presetRoute = req.path.match(/^\/Duluka\/v\.1\.0\/GameFilter\/Preset\/(Select|Save|Toggle)$/);
    const diagnostic = focus || settingsRoute || !!presetRoute || req.path === '/Duluka/v.1.0/GameFilter/State';
    if (!match && !diagnostic) return next();
    const route = presetRoute ? '/Preset/' + presetRoute[1] : diagnostic ? '/State' : match[1] || '/';
    try {
      const body = req.method === 'POST' ? await readBody(req) : {};
      if (settingsRoute) {
        if (!['GET','POST'].includes(req.method)) throw failure(405, 'METHOD_NOT_ALLOWED', 'GET or POST is required');
        if (req.method === 'POST') {
          if (!validSettings(body) || Object.keys(body).some(key => !Object.prototype.hasOwnProperty.call(defaults, key)))
            throw failure(400, 'INVALID_ARGUMENT', 'Valid mode and boolean connection settings are required');
          const updated = {mode:body.mode,allowConnection:body.allowConnection,connectionManager:body.connectionManager};
          const temporary = settingsPath + '.tmp';
          fs.writeFileSync(temporary, JSON.stringify(updated, null, 2), 'utf8');
          fs.renameSync(temporary, settingsPath);
          if (JSON.stringify(updated) !== JSON.stringify(configuration)) configurationVersion++;
          configuration = updated;
          if (!bridgeEnabled()) {
            pendingEnable = undefined;
            stateRead = null;
            disconnected(failure(503, 'BRIDGE_DISABLED', 'ReShade connection is disabled'));
          }
        }
        return res.json(Object.assign({bridgeEnabled:bridgeEnabled()}, configuration));
      }
      if (focus) {
        if (req.method !== 'POST') throw failure(405, 'METHOD_NOT_ALLOWED', 'POST is required');
        const address = req.socket.remoteAddress;
        if (!['127.0.0.1', '::1', '::ffff:127.0.0.1'].includes(address)) throw failure(403, 'FORBIDDEN', 'Local helper only');
        if (!Number.isInteger(body.pid) || body.pid < 0 || typeof body.executable !== 'string' || typeof body.title !== 'string')
          throw failure(400, 'INVALID_ARGUMENT', 'Foreground process information is required');
        const executable = body.executable.slice(0, 128), title = body.title.slice(0, 256);
        const game = last && last.pid === body.pid;
        const overlay = /^NVIDIA OSC$/i.test(executable);
        const games = await gameWatcherGames();
        const matched = games.find(item => item.processId === body.pid) ||
          (overlay && games.find(item => last && item.processId === last.pid));
        const message = game ? 'Overlay focus: ' + (title || executable) : overlay ? 'Overlay UI' : 'Desktop: ' + (executable || 'No foreground application');
        if (io) io.emit('/Duluka/v1/OverlayGameEntry', { message, pid:body.pid,
          executablePath:matched ? matched.executablePath : game || overlay && last ? last.executable : '',
          displayName:matched ? matched.displayName : '',
          subtext:overlay && last ? 'Game target: ' + last.executable : 'PID ' + body.pid });
        return res.json({success:true,message});
      }
      if (req.method !== 'GET' && req.method !== 'POST') throw failure(405, 'METHOD_NOT_ALLOWED', 'GET or POST is required');
      const pidText = req.headers['x-ansel-game-pid'];
      const pid = pidText === undefined ? undefined : Number(pidText);
      if (pid !== undefined && (!Number.isInteger(pid) || pid <= 0)) throw failure(400, 'INVALID_ARGUMENT', 'Invalid game process ID');

      // Feature support describes implemented operations, not fabricated runtime
      // availability. Combined/batched setters remain disabled until atomic.
      if (route === '/Compatible') return res.json({ compatible: true, version: 'Ansel Adapter protocol 1' });
      if (route === '/SetFilterAndAttributesSupported') return res.json({ setFreestyleAndAttributesSupported: false });
      if (route === '/SetMultipleFilterAPISupport') return res.json({ isSetMultipleFilterAPISupported: true });
      if (route === '/GetNvCameraConfig') return res.json({ enabled: last ? last.enabled : true, ipcEnabled: true, freestyleEnabled: true });
      if (route === '/IPC' || route === '/Language' || route === '/GameEngine' || route === '/uiReady') return res.json({ success: true });
      if (!bridgeEnabled()) {
        if (route === '/GetAvailable') return res.json({available:false});
        if (route === '/ReshadeSupported') return res.json({reshadeSupported:false});
        if (route === '/GetFreestyleSupport') return res.json({freestyleWhitelisted:false,anselWhitelisted:false,supported:false});
        if (route === '/State') return res.status(503).json({connected:false,code:'BRIDGE_DISABLED',message:'ReShade connection is disabled in Game Filter settings'});
        throw failure(503, 'BRIDGE_DISABLED', 'ReShade connection is disabled in Game Filter settings');
      }
      let target;
      const version = configurationVersion;
      const mutation = route === '/EnableMods' || route.startsWith('/Preset/') ||
        /^\/Filter(?:\/|$)/.test(route) && route !== '/Filter/GetInfo';
      if (mutation && req.method !== 'POST') throw failure(405, 'METHOD_NOT_ALLOWED', 'Mutations require POST');
      try {
        if (mutation && needsSession && !pid && last) throw failure(409, 'STALE_SESSION', 'Reopen the filter editor after the runtime changes');
        if (editingTarget && (!pid || pid === editingTarget.state.pid) && (mutation || route === '/GetFilters' || route === '/Filter/GetInfo'))
          target = editingTarget;
        else target = await cachedState(pid,
          // Opening the panel often calls GetAvailable, then Capture/Control.
          // Avoid two expensive catalog walks in the same opening sequence;
          // retain a fresh read for older editing sessions and explicit refresh.
          (route === '/Capture/Control' && body.capture === true && Date.now() - cachedAt > 5000) ||
          (route === '/State' && (
            req.query && req.query.refresh === '1' ||
            // An open editor must reflect changes made in ReShade itself.
            // Keep idle menu/availability reads cheap; the editor heartbeat
            // performs at most one complete catalog read every two seconds.
            editingTarget && Date.now() - cachedAt >= 1800
          )), version);
        requireBridge(version);
      }
      catch (error) {
        if (error.code === 'BRIDGE_DISABLED' || error.code === 'BRIDGE_CHANGED') throw error;
        if (error.code !== 'STALE_SESSION') disconnected(error);
        if (route === '/EnableMods') {
          const value = body.globalEnable === undefined ? (body.enabled === undefined ? body.enable : body.enabled) : body.globalEnable;
          if (typeof value !== 'boolean') throw failure(400, 'INVALID_ARGUMENT', 'globalEnable must be a boolean');
          pendingEnable = value;
          return res.json({ success: true, enabled: value, applied: false });
        }
        if (route === '/ReshadeSupported') return res.json({ reshadeSupported: false });
        if (route === '/GetFreestyleSupport') return res.json({ freestyleWhitelisted: false, anselWhitelisted: false, supported: false });
        if (route === '/GetAvailable') { emit({ type: 'available', features: [] }); return res.json({ available: false }); }
        if (route === '/State') return res.status(error.status || 503).json({ connected: false, code: error.code, message: error.message });
        throw error;
      }
      const state = target.state;
      cachedTarget = target;
      if (last !== state) publish(state);
      if (route === '/State') return res.json(Object.assign({ connected: true }, state));
      if (route === '/ReshadeSupported') return res.json({ reshadeSupported: true });
      if (route === '/GetFreestyleSupport') return res.json({ freestyleWhitelisted: true, anselWhitelisted: false, supported: true });
      if (route === '/GetAvailable') {
        emit({ type: 'available', features: ['mods', 'allowOffline'] });
        return res.json({ available: true });
      }
      if (route === '/GetIntegration') return res.json({ integrated: true });
      if (route === '/GetProcessInfo') {
        const info = { type: 'procInfo', procId: state.pid, procPath: state.executable, processId: state.pid, pid: state.pid, exe: state.executable,
          processName: state.executable, name: state.executable, graphicsAPI: 11 };
        emit(info); return res.json(info);
      }
      if (route === '/GetFilters') {
        const filters = state.filters.map(f => ({ id: f.id, name: f.name }));
        emit({ type: 'filters', filters }); return res.json({ filters });
      }
      if (route === '/Capture/Control') {
        if (typeof body.capture !== 'boolean') throw failure(400, 'INVALID_ARGUMENT', 'capture must be a boolean');
        // This is the filter-editing session only, not photography/game pausing.
        editingTarget = body.capture ? target : null;
        needsSession = false;
        emit({ type: 'captureControlChange', capture: 1 });
        return res.json({ success: true });
      }
      if (route === '/Preset/Select' || route === '/Preset/Save' || route === '/Preset/Toggle') {
        if (req.method !== 'POST') throw failure(405, 'METHOD_NOT_ALLOWED', 'POST is required');
        if (!state.preset || state.preset.supported !== true)
          throw failure(501, 'NOT_SUPPORTED', 'This runtime does not support ReShade INI presets');
        if (route === '/Preset/Select' && (!Number.isInteger(body.slot) || body.slot < 1 || body.slot > 3))
          throw failure(400, 'INVALID_ARGUMENT', 'Preset slot must be 1, 2 or 3');
        if (route === '/Preset/Toggle' && typeof body.enabled !== 'boolean')
          throw failure(400, 'INVALID_ARGUMENT', 'enabled must be a boolean');
        // ReShade saves the outgoing preset when switching; a previously
        // scheduled write must never accidentally target the incoming slot.
        cancelPresetSave();
        const updated = await client.command(target,
          route === '/Preset/Select' ? 'presetSelect' : route === '/Preset/Save' ? 'presetSave' : 'presetToggle',
          route === '/Preset/Select' ? {slot:body.slot} : route === '/Preset/Toggle' ? {enabled:body.enabled} : {},
          () => requireBridge(version));
        requireBridge(version);
        cachedTarget = {name:target.name,state:updated};
        editingTarget = editingTarget && editingTarget.name === target.name ? cachedTarget : editingTarget;
        lastCatalog = ''; // Force NVIDIA's filter listener to rebuild the list.
        publish(updated);
        updated.stack.forEach((_, idx) => emit(currentFilter(updated, idx)));
        return res.json({success:true,preset:updated.preset,state:updated});
      }
      if (route === '/Filter/GetInfo') {
        if (!Number.isInteger(body.stackIdx) || body.stackIdx < 0 || body.stackIdx >= state.stack.length)
          throw failure(404, 'FILTER_NOT_FOUND', 'Stack slot is empty');
        const info = currentFilter(state, body.stackIdx); emit(info); return res.json(info);
      }
      let op, command = body, event;
      const attribute = route.match(/^\/Filter\/(.+)\/Attribute$/);
      if (route === '/Filter') { op = 'select'; event = 'currentFilter'; }
      else if (route === '/Filter/Remove') { op = 'select'; command = Object.assign({}, body, { type: 'None' }); event = 'currentFilter'; }
      else if (attribute) { op = 'attribute'; command = Object.assign({}, body, { id: decodeURIComponent(attribute[1]) }); event = 'setFilterAttribute'; }
      else if (route === '/Filter/ResetStack' || route === '/Filter/ResetAll') { op = 'clear'; event = 'resetEntireStack'; }
      else if (route === '/Filter/Reset') { op = 'reset'; event = 'resetFilter'; }
      else if (route === '/Filter/setMultipleFiltersAndAttributes') { op = 'replace'; event = 'filterAndAttributes'; }
      else if (route === '/EnableMods') {
        op = 'mods';
        const enabled = body.globalEnable === undefined ? (body.enabled === undefined ? body.enable : body.enabled) : body.globalEnable;
        command = { globalEnable: enabled };
      } else throw failure(501, 'NOT_SUPPORTED', 'This NvCamera operation is not supported by Game Filter');
      if (req.method !== 'POST') throw failure(405, 'METHOD_NOT_ALLOWED', 'Mutations require POST');
      requireBridge(version);
      const updated = await client.command(target, op, command, () => requireBridge(version));
      requireBridge(version);
      cachedTarget = { name: target.name, state: updated };
      if (op === 'mods') pendingEnable = undefined;
      publish(updated, event === 'currentFilter' || event === 'filterAndAttributes');
      if (editingTarget && editingTarget.name === target.name) editingTarget = { name: target.name, state: updated };
      if (['select','attribute','replace','reset','clear'].includes(op))
        schedulePresetSave(cachedTarget, version);
      if (event === 'currentFilter') emit(currentFilter(updated, body.stackIdx));
      else if (event === 'filterAndAttributes') {
        updated.stack.forEach((_, slot) => {
          emit(currentFilter(updated, slot));
          emit(currentFilter(updated, slot, event));
        });
        if (!updated.stack.length) emit({ type: event, status: 1, stackIdx: -1, controls: [] });
        emit({ type: 'multipleSetFilterAndAttributes', status: 1 });
      }
      else if (event) emit({ type: event, status: 1, stackIdx: body.stackIdx });
      res.json({ success: true, enabled: updated.enabled, session: updated.session, generation: updated.generation });
    } catch (error) {
      if (error.code === 'STALE_SESSION') {
        cachedTarget = null;
        editingTarget = null;
        needsSession = true;
      }
      res.status(error.status || 500).json({ type: 'Error', code: error.status || 500,
        codeText: error.code || 'ANSEL_ERROR', message: error.message });
    }
  });
  return { close: () => { stopped = true; cancelPresetSave(); if (timer) clearInterval(timer); }, poll };
}
module.exports = mount;
