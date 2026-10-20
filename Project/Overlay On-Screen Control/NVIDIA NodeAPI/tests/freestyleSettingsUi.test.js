'use strict';

const assert = require('assert');
const { createFilterSettingsModel, filterSettingsTemplate } = require('../osc/mods/gpu-status-panel');

const endpoint = '/Duluka/v.1.0/GameFilter/Settings';
const enabled = { mode: 'reshade', allowConnection: true, connectionManager: true, bridgeEnabled: true };

function deferred() {
  let resolve, reject;
  const promise = new Promise((onResolve, onReject) => { resolve = onResolve; reject = onReject; });
  return { promise, resolve, reject };
}

function fakeHttp() {
  const requests = [];
  return {
    requests,
    get(url, config) {
      const pending = deferred();
      requests.push({ method: 'GET', url, config, pending });
      return pending.promise;
    },
    post(url, body, config) {
      const pending = deferred();
      requests.push({ method: 'POST', url, body: JSON.parse(JSON.stringify(body)), config, pending });
      return pending.promise;
    }
  };
}

function values(model) {
  return { mode: model.mode, allowConnection: model.allowConnection, connectionManager: model.connectionManager };
}

function publicState(model) {
  return Object.assign(values(model), {
    loaded: model.loaded, busy: model.busy, status: model.status, error: model.error, retryLabel: model.retryLabel
  });
}

async function settle(result) {
  try { return await result; } catch (error) { return error; }
}

function assertRequest(request, method) {
  assert(request, method + ' request was not sent');
  assert.strictEqual(request.method, method);
  assert.strictEqual(request.url, endpoint);
  assert.strictEqual(request.config.timeout, 5000, method + ' must not leave controls disabled indefinitely');
}

async function load(model, http, response = enabled) {
  const before = http.requests.length;
  const result = model.load();
  assert.strictEqual(http.requests.length, before + 1);
  const request = http.requests[before];
  assertRequest(request, 'GET');
  request.pending.resolve({ data: response });
  await settle(result);
}

async function initialAndLoad() {
  const http = fakeHttp();
  const model = createFilterSettingsModel(http);
  assert.strictEqual(model.loaded, false, 'defaults must not be presented as confirmed settings');
  assert.deepStrictEqual(values(model), { mode: 'reshade', allowConnection: true, connectionManager: true });
  await settle(model.save());
  assert.strictEqual(http.requests.length, 0, 'loading defaults must not trigger a save');
  await load(model, http);
  assert.strictEqual(model.loaded, true);
  assert.strictEqual(model.busy, false);
  assert(!model.error);
  assert.strictEqual(http.requests.length, 1, 'successful load must not autosave');
  assert(!/^(?:ReShade is connected|Connected to ReShade|ReShade connection enabled)[.!]?$/i.test(model.status),
    'saved permission must not claim a live ReShade connection');
  model.dispose();
}

async function loadFailureAndRetry() {
  const http = fakeHttp();
  const model = createFilterSettingsModel(http);
  const first = model.load();
  assertRequest(http.requests[0], 'GET');
  http.requests[0].pending.reject({ status: -1, message: 'timeout' });
  await settle(first);
  assert.strictEqual(model.loaded, false);
  assert.strictEqual(model.busy, false);
  assert(model.error, 'load error must be visible');
  assert(model.retryLabel, 'failed loading needs a usable retry action');
  const retry = model.retry();
  assertRequest(http.requests[1], 'GET');
  http.requests[1].pending.resolve({ data: enabled });
  await settle(retry);
  assert.strictEqual(model.loaded, true);
  assert(!model.error, 'successful load retry must clear the old error');
  model.dispose();
}

async function invalidResponses() {
  const invalid = [
    null, [], {},
    Object.assign({}, enabled, { mode: 'unknown' }),
    Object.assign({}, enabled, { allowConnection: 'true' }),
    Object.assign({}, enabled, { connectionManager: 1 }),
    Object.assign({}, enabled, { bridgeEnabled: undefined }),
    Object.assign({}, enabled, { bridgeEnabled: 'true' }),
    Object.assign({}, enabled, { bridgeEnabled: false }),
    Object.assign({}, enabled, { mode: 'duluka' }),
    Object.assign({}, enabled, { allowConnection: false }),
    Object.assign({}, enabled, { connectionManager: false })
  ];
  for (const response of invalid) {
    const http = fakeHttp();
    const model = createFilterSettingsModel(http);
    await load(model, http, response);
    assert.strictEqual(model.loaded, false, 'invalid server settings became editable: ' + JSON.stringify(response));
    assert(model.error, 'invalid server settings need a visible error');
    assert(model.retryLabel);
    assert.strictEqual(http.requests.filter(request => request.method === 'POST').length, 0);
    model.dispose();
  }
}

async function saveRollbackRetryAndBusyGuard() {
  const http = fakeHttp();
  const model = createFilterSettingsModel(http);
  await load(model, http);
  model.allowConnection = false;
  const first = model.save();
  assert.strictEqual(model.busy, true, 'save must synchronously block repeated interactions');
  const post = http.requests[1];
  assertRequest(post, 'POST');
  const attempted = { mode: 'reshade', allowConnection: false, connectionManager: true };
  assert.deepStrictEqual(post.body, attempted);
  const duplicate = model.save();
  assert.strictEqual(http.requests.length, 2, 'simultaneous saves queued duplicate writes');
  post.pending.reject({ status: 503 });
  await Promise.all([settle(first), settle(duplicate)]);
  assert.strictEqual(model.busy, false);
  assert.strictEqual(model.loaded, true, 'a failed save must retain confirmed settings');
  assert.deepStrictEqual(values(model), { mode: 'reshade', allowConnection: true, connectionManager: true },
    'failed save must roll back to last server-confirmed values');
  assert(model.error);
  assert(model.retryLabel);

  const retry = model.retry();
  const retried = http.requests[2];
  assertRequest(retried, 'POST');
  assert.deepStrictEqual(retried.body, attempted, 'retry saved the rolled-back values instead of the failed change');
  retried.pending.resolve({ data: Object.assign({}, attempted, { bridgeEnabled: false }) });
  await settle(retry);
  assert.deepStrictEqual(values(model), attempted);
  assert(!model.error, 'successful save retry must clear the old error');
  assert.strictEqual(model.busy, false);

  model.connectionManager = false;
  const invalid = model.save();
  const invalidPost = http.requests[3];
  invalidPost.pending.resolve({ data: Object.assign({}, attempted, { connectionManager: false, bridgeEnabled: true }) });
  await settle(invalid);
  assert.deepStrictEqual(values(model), attempted, 'inconsistent save response overwrote confirmed settings');
  assert(model.error);

  model.allowConnection = true;
  const recovery = model.save();
  const recoveryPost = http.requests[4];
  recoveryPost.pending.resolve({ data: enabled });
  await settle(recovery);
  assert(!model.error, 'a later successful change retained a stale error');
  assert.deepStrictEqual(values(model), { mode: 'reshade', allowConnection: true, connectionManager: true });
  model.dispose();
}

async function modeAvailability() {
  for (const mode of ['duluka', 'nvidia']) {
    const http = fakeHttp();
    const model = createFilterSettingsModel(http);
    await load(model, http, Object.assign({}, enabled, { mode, bridgeEnabled: false }));
    assert.strictEqual(model.loaded, true, 'unavailable but valid mode must still display saved preferences');
    assert.strictEqual(model.mode, mode);
    assert(/unavailable|not (?:yet )?available|coming soon/i.test(model.status),
      mode + ' must not be presented as implemented: ' + model.status);
    assert(!/^ReShade connection/i.test(model.status), 'other modes must not use a ReShade status label');
    model.dispose();
  }
}

async function disposeIgnoresLateResults() {
  for (const action of ['load-success', 'load-error', 'save-success', 'save-error']) {
    const http = fakeHttp();
    const model = createFilterSettingsModel(http);
    if (action.indexOf('save') === 0) await load(model, http);
    model.allowConnection = false;
    const pendingResult = action.indexOf('save') === 0 ? model.save() : model.load();
    const request = http.requests[http.requests.length - 1];
    model.dispose();
    const disposedState = publicState(model);
    if (action.endsWith('success')) request.pending.resolve({ data: enabled });
    else request.pending.reject({ status: 500 });
    await settle(pendingResult);
    assert.deepStrictEqual(publicState(model), disposedState, action + ' updated a destroyed page');
    const count = http.requests.length;
    await settle(model.load());
    await settle(model.save());
    await settle(model.retry());
    assert.strictEqual(http.requests.length, count, 'disposed models must not start new requests');
  }
}

function nativeTemplate() {
  const template = typeof filterSettingsTemplate === 'function' ? filterSettingsTemplate() : filterSettingsTemplate;
  assert.strictEqual(typeof template, 'string');
  assert(/<md-tabs\b/.test(template), 'the two feature tabs must use the existing NVIDIA Material component');
  assert(/label=["']Game filter["']/.test(template));
  assert(/label=["']Photo mode["']/.test(template));
  assert(/<md-select\b[^>]*class=["'][^"']*\bosc-select\b/.test(template), 'mode select must use native NVIDIA visuals');
  assert((template.match(/<md-switch\b/g) || []).length >= 2, 'both ReShade controls must use native switches');
  assert(/<md-switch\b[^>]*ng-disabled=/.test(template), 'settings must remain disabled before confirmed loading and during saves');
  assert(/aria-live=["']polite["']/.test(template), 'async status must be announced accessibly');
  assert(/ng-click=["']filterSettings\.retry\(\)["']/.test(template), 'retry action must be reachable without reopening the page');
  const installer = template.match(/<button\b[^>]*>\s*Install ReShade\s*<\/button>/);
  assert(installer, 'installer placeholder is missing');
  assert(/\bdisabled(?:\s|=|>)/.test(installer[0]), 'installer UI must not promise an implemented action');
  assert(/\bosc-button-disabled\b/.test(installer[0]), 'installer must use native disabled appearance');
}

async function main() {
  await initialAndLoad();
  await loadFailureAndRetry();
  await invalidResponses();
  await saveRollbackRetryAndBusyGuard();
  await modeAvailability();
  await disposeIgnoresLateResults();
  nativeTemplate();
  console.log('PASS: Freestyle preferences UI loading, validated persistence, timeout/retry, rollback, repeated saves, disposed requests, honest availability and NVIDIA template semantics');
}

main().catch(error => { console.error(error); process.exitCode = 1; });
