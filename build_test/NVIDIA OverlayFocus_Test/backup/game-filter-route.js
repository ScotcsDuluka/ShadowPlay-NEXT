'use strict';
const { AnselClient, failure } = require('./ansel-client');
const CHANNEL = '/NvCamera/v.1.0/Notifications';

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
  const client = options.client || new AnselClient();
  let last = null, lastCatalog = '', stopped = false, polling = false, pendingEnable, editingTarget = null, needsSession = false;
  let cachedTarget = null, stateRead = null;
  // Availability probes must not enumerate thousands of uniforms on the game
  // render thread. Interaction reads and verified command replies refresh this.
  async function cachedState(pid, refresh) {
    if (!refresh && cachedTarget && (!pid || cachedTarget.state.pid === pid)) {
      const names = await client.list();
      const eligible = names.filter(name => /^NVIDIAAnsel\.\d+\.\d+$/.test(name) && (!pid || name.startsWith('NVIDIAAnsel.' + pid + '.')));
      if (eligible.length === 1 && eligible[0] === cachedTarget.name) return cachedTarget;
      cachedTarget = null;
    }
    if (stateRead && stateRead.pid === pid) return stateRead.promise;
    const flight = { pid };
    flight.promise = client.state(pid).then(target => {
      cachedTarget = target;
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
    if (!suppressCurrent) state.stack.forEach((_, index) => {
      const before = last && currentFilter(last, index), after = currentFilter(state, index);
      if (JSON.stringify(before) !== JSON.stringify(after)) emit(after);
    });
    last = state;
  }
  function disconnected(error) {
    if (last) {
      emit({ type: 'anselState', connected: false, processId: last.pid, reason: error.code });
      emit({ type: 'available', features: [] });
      emit({ type: 'filters', filters: [] });
    }
    if (editingTarget) needsSession = true;
    editingTarget = null; last = null; lastCatalog = ''; cachedTarget = null;
  }
  async function poll() {
    if (stopped || polling) return;
    polling = true;
    try {
      // Only inspect pipe names while idle; a full snapshot is expensive even
      // when the panel is closed. Pending preferences need one real command.
      const names = await client.list();
      if (cachedTarget && !names.includes(cachedTarget.name)) disconnected(failure(503, 'ANSEL_UNAVAILABLE', 'Game runtime disconnected'));
      if (pendingEnable === undefined || !names.some(name => /^NVIDIAAnsel\.\d+\.\d+$/.test(name))) return;
      const target = await client.state();
      let state = target.state;
      if (pendingEnable !== undefined) {
        state = await client.command(target, 'mods', { globalEnable: pendingEnable });
        pendingEnable = undefined;
      }
      publish(state);
      cachedTarget = { name: target.name, state };
      if (editingTarget) editingTarget = { name: target.name, state };
    }
    catch (error) { disconnected(error); }
    finally { polling = false; }
  }
  const timer = options.poll === false ? null : setInterval(poll, 1000);
  if (timer && timer.unref) timer.unref();

  app.use(async function gameFilter(req, res, next) {
    const match = req.path.match(/^\/NvCamera\/v\.1\.[01](\/.*)?$/);
    const diagnostic = req.path === '/Duluka/v.1.0/GameFilter/State';
    if (!match && !diagnostic) return next();
    const route = diagnostic ? '/State' : match[1] || '/';
    try {
      const body = req.method === 'POST' ? await readBody(req) : {};
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
      let target;
      const mutation = route === '/EnableMods' || /^\/Filter(?:\/|$)/.test(route) && route !== '/Filter/GetInfo';
      if (mutation && req.method !== 'POST') throw failure(405, 'METHOD_NOT_ALLOWED', 'Mutations require POST');
      try {
        if (mutation && needsSession && !pid && last) throw failure(409, 'STALE_SESSION', 'Reopen the filter editor after the runtime changes');
        if (editingTarget && (!pid || pid === editingTarget.state.pid) && (mutation || route === '/GetFilters' || route === '/Filter/GetInfo'))
          target = editingTarget;
        else target = await cachedState(pid,
          route === '/GetAvailable' || route === '/Capture/Control' && body.capture === true ||
          route === '/State' && req.query && req.query.refresh === '1');
      }
      catch (error) {
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
      const updated = await client.command(target, op, command);
      cachedTarget = { name: target.name, state: updated };
      if (op === 'mods') pendingEnable = undefined;
      publish(updated, event === 'currentFilter' || event === 'filterAndAttributes');
      if (editingTarget && editingTarget.name === target.name) editingTarget = { name: target.name, state: updated };
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
  return { close: () => { stopped = true; if (timer) clearInterval(timer); }, poll };
}
module.exports = mount;
