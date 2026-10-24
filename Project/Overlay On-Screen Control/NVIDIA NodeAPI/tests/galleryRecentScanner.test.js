'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const scanner = require('../shims/gallery-recent-scanner');

function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'nv-gallery-scan-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  return root;
}
function run(root, limit) {
  return new Promise((resolve, reject) =>
    scanner.recent(root, limit, (err, result) => err ? reject(err) : resolve(result)));
}
test('recent includes nested NVIDIA and Captures recordings, newest first', async t => {
  const root = fixture(t);
  for (const folder of ['Captures', 'NVIDIA', path.join('NVIDIA', 'Game')]) {
    fs.mkdirSync(path.join(root, folder), { recursive: true });
  }
  const first = path.join(root, 'old.mp4');
  const newest = path.join(root, 'NVIDIA', 'Game', 'new.mp4');
  const screenshot = path.join(root, 'Captures', 'screenshot.png');
  fs.writeFileSync(first, 'old');
  fs.writeFileSync(newest, 'new');
  fs.writeFileSync(screenshot, 'pic');
  fs.writeFileSync(path.join(root, 'Captures', 'notes.txt'), 'ignore');
  const now = Date.now() / 1000;
  fs.utimesSync(first, now - 200, now - 200);
  fs.utimesSync(screenshot, now - 100, now - 100);
  fs.utimesSync(newest, now, now);
  const result = await run(root, 2);
  assert.deepEqual(result.files, [newest, screenshot]);
});

test('recents reject bad root and return empty list for zero count', async t => {
  const root = fixture(t);
  assert.deepEqual(await run(root, 0), { files: [] });
  await assert.rejects(run(path.join(root, 'missing'), 2));
  await assert.rejects(run('', 2));
});
test('nested folders that disappear are skipped and do not blank gallery', async t => {
  const root = fixture(t);
  fs.mkdirSync(path.join(root, 'gone'));
  fs.writeFileSync(path.join(root, 'safe.mp4'), 'safe');
  const found = await run(root, 10);
  assert.deepEqual(found.files, [path.join(root, 'safe.mp4')]);
});
test('scanner ignores symlinked folders to prevent leakage outside recordings', async t => {
  const root = fixture(t);
  const external = fixture(t);
  fs.writeFileSync(path.join(external, 'hidden.mp4'), 'secret');
  try { fs.symlinkSync(external, path.join(root, 'linked'), 'junction'); }
  catch (e) { t.skip('Platform cannot create directory symlink: ' + e.code); return; }
  assert.deepEqual((await run(root, 100)).files, []);
});
