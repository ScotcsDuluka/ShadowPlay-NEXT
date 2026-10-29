'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const osc = path.resolve(__dirname, '..', 'osc');
const source = fs.readFileSync(path.join(osc, 'Module', 'Gallery', 'Media', 'gallery-media-source.js'), 'utf8');

class Events {
  constructor() { this.listeners = Object.create(null); }
  addEventListener(name, callback) {
    (this.listeners[name] = this.listeners[name] || []).push(callback);
  }
  removeEventListener(name, callback) {
    const arr = this.listeners[name] || [];
    const index = arr.indexOf(callback);
    if (index >= 0) arr.splice(index, 1);
  }
  dispatchEvent(event) {
    for (const f of (this.listeners[event.type] || []).slice()) f(event);
  }
  fire(type) { this.dispatchEvent({type}); }
}
function mock() {
  const requests = [];
  const media = [];
  const observers = [];
  const video = new Events();
  video.tagName = 'VIDEO';
  video.currentTime = 0;
  video.autoplay = false;
  video.src = '';
  video.play = () => Promise.resolve();
  video.load = () => {};
  video.getAttribute = key => key === 'src' ? video.src : null;
  const sourceNode = {
    nodeType: 1, tagName: 'SOURCE', parentElement: video,
    getAttribute: key => key === 'src' ? 'file:///C:/Videos/recording.mp4' : null
  };
  const document = {
    documentElement: {
      nodeType: 1,
      matches: () => false,
      querySelectorAll: selector => selector === 'video[src],source[src]' ? [sourceNode] : [],
      contains: element => element === video
    }
  };
  class Buffer extends Events {
    constructor() {
      super(); this.updating = false; this.mode = null; this.timestampOffset = 0;
      this.ranges = [];
      this.buffered = {
        get length() { return this._owner.ranges.length; },
        start: i => this.ranges[i][0],
        end: i => this.ranges[i][1],
        _owner: this
      };
      this.bytes = [];
      this.aborts = 0;
    }
    appendBuffer(bytes) {
      this.bytes.push(bytes);
      this.updating = true;
      const at = Number(new TextDecoder().decode(bytes));
      this.ranges.push([at, at + 8]);
      setImmediate(() => { this.updating = false; this.fire('updateend'); });
    }
    abort() { this.aborts++; }
    remove(start,end) {
      this.updating = true;
      this.ranges = this.ranges.filter(item => item[1] > end);
      setImmediate(() => { this.updating = false; this.fire('updateend'); });
    }
  }
  class Media extends Events {
    constructor() { super();this.buffers=[];media.push(this); }
    static isTypeSupported() { return true; }
    addSourceBuffer() {
      const buffer = new Buffer();
      this.buffers.push(buffer);
      return buffer;
    }
  }
  function fetch(url, options) {
    if (url.includes('/PlaybackInfo?')) return Promise.resolve({
      ok: true,
      json: () => Promise.resolve({ duration: 56, segmentDuration: 8, audio: false })
    });
    const query = new URL(url).searchParams;
    return new Promise((resolve,reject) => {
      const value = {
        start: Number(query.get('segment')),
        track: query.get('track'),
        aborted: false,
        resolve() {
          resolve({ok:true,arrayBuffer:() => Promise.resolve(new TextEncoder().encode(String(value.start)))});
        }
      };
      requests.push(value);
      if (options && options.signal) {
        options.signal.addEventListener('abort', () => {
          value.aborted = true;
          const err = new Error('aborted');
          err.name = 'AbortError';
          reject(err);
        });
      }
    });
  }
  const context = {
    URL, TextDecoder, TextEncoder, console,
    window: {MediaSource: Media, AbortController},
    MediaSource: Media, AbortController,
    CustomEvent: class {constructor(type){this.type=type;}},
    document, fetch,
    location: {href:'http://127.0.0.1:59011/osc/index.html'},
    MutationObserver: class {
      constructor(cb){this.cb=cb;this.active=false;observers.push(this);}
      observe() { this.active=true; }
      disconnect() { this.active=false; }
    }
  };
  context.URL.createObjectURL = object => {
    setImmediate(() => object.fire('sourceopen'));
    return 'blob:mock-gallery-video';
  };
  context.URL.revokeObjectURL = () => {};
  vm.runInNewContext(source, context);
  return {video, requests, media, observers, sourceNode,
    reload: () => vm.runInNewContext(source, context)};
}
function tick() { return new Promise(resolve => setImmediate(resolve)); }
test('the real OSC HTML includes Gallery MSE bridge exactly once', () => {
  const html = fs.readFileSync(path.join(osc,'index.html'),'utf8');
  const count = (html.match(/src="\/Module\/Gallery\/Media\/gallery-media-source\.js\?v=/g) || []).length;
  assert.equal(count, 1);
});
test('repeated script injection replaces the observer and preserves playback', async () => {
  const env = mock();
  await tick(); await tick();
  const playback = env.video.__nvSegmentPlayback;
  env.reload();
  assert.equal(env.observers.length, 2);
  assert.equal(env.observers[0].active, false);
  assert.equal(env.observers[1].active, true);
  assert.equal(env.video.__nvSegmentPlayback, playback);
  assert.equal(playback.disposed, false);
  env.video.__nvSegmentPlayback.dispose();
});
test('seeking cancels obsolete segment and prioritizes new timeline', async () => {
  const env = mock();
  await tick(); await tick();
  assert.equal(env.requests.length, 1);
  assert.equal(env.requests[0].start, 0);
  env.video.currentTime = 24;
  env.video.fire('seeking');
  await tick();
  assert.equal(env.requests[0].aborted, true);
  assert.ok(env.requests.some(r => r.start === 24));
  const wanted = env.requests.find(r => r.start === 24);
  wanted.resolve();
  await tick(); await tick();
  assert.ok(env.media[0].buffers[0].bytes.length);
  assert.equal(env.media[0].buffers[0].aborts, 0);
  env.video.__nvSegmentPlayback.dispose();
  assert.equal(env.video.__nvSegmentPlayback, null);
});
test('source switch disposes previous instance and allows fresh playback', async () => {
  const env = mock();
  await tick();await tick();
  const old = env.video.__nvSegmentPlayback;
  env.sourceNode.getAttribute = () => 'file:///C:/Videos/second.mp4';
  env.observers[0].cb([{type:'attributes', target:env.sourceNode}]);
  assert.equal(old.disposed, true);
  assert.notEqual(env.video.__nvSegmentPlayback, old);
  env.video.__nvSegmentPlayback.dispose();
});
