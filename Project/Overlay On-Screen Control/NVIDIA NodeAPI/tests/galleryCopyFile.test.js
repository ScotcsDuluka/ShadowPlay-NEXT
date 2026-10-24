'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const copy = require('../shims/gallery-copy-file').copy;

function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'nv-gallery-copy-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  return root;
}
function execute(options, resolver) {
  return new Promise(resolve => copy(options, resolver, (error, result) =>
    resolve({ error, result })));
}
test('copies real media file and never fakes success', async t => {
  const root = fixture(t);
  const src = path.join(root, 'clip.mp4');
  const dest = path.join(root, 'copied.mp4');
  fs.writeFileSync(src, Buffer.from('actual recording bytes'));
  const actual = await execute({ source: src, destination: dest },
    (_file, callback) => callback(null, { filename: src }));
  assert.equal(actual.error, null);
  assert.deepEqual(actual.result, { copied: true });
  assert.deepEqual(fs.readFileSync(dest), fs.readFileSync(src));
});

test('refuses overwrite and preserves previously copied bytes', async t => {
  const root = fixture(t);
  const source = path.join(root, 'clip.mp4');
  const target = path.join(root, 'keep.mp4');
  fs.writeFileSync(source, 'recording');
  fs.writeFileSync(target, 'existing');
  const result = await execute({ source, destination: target },
    (_, callback) => callback(null, { filename: source }));
  assert.equal(result.error.statusCode, 409);
  assert.equal(fs.readFileSync(target, 'utf8'), 'existing');
});

test('unavailable source, relative destination, and wrong extension fail safely', async t => {
  const root = fixture(t);
  const source = path.join(root, 'lost.mp4');
  const target = path.join(root, 'save.mp4');
  const bad = await execute({ source, destination: target },
    (_, callback) => callback({ statusCode: 404 }));
  assert.equal(bad.error.statusCode, 404);
  assert.equal(fs.existsSync(target), false);

  const reject = (_, callback) => callback(null, { filename: source });
  assert.equal((await execute({ source, destination: 'relative.mp4' }, reject)).error.statusCode, 400);
  assert.equal((await execute({ source, destination: path.join(root, 'evil.exe') }, reject)).error.statusCode, 400);
});
