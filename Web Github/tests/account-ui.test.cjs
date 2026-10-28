'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(__dirname, '..');
const page = fs.readFileSync(path.join(root, 'account.html'), 'utf8');
const css = fs.readFileSync(path.join(root, 'CSS', 'account.css'), 'utf8');
const script = fs.readFileSync(path.join(root, 'JS', 'account.js'), 'utf8');
const home = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
const portal = 'https://aorus-desktop.taile6314b.ts.net/portal';

test('home links directly to the live account system, preserving original hero', () => {
  assert.match(home, /href="https:\/\/aorus-desktop\.taile6314b\.ts\.net\/portal"[^>]*>ACCOUNT/);
  assert.match(home, /href="https:\/\/aorus-desktop\.taile6314b\.ts\.net\/portal">Duluka Account/);
  assert.match(home, /id="obt3-banner"/);
  assert.match(page, /href="index\.html"/);
});
test('account pages link to the known HTTPS server, not a fake static login form', () => {
  assert.equal((page.match(new RegExp('href="'+portal+'"', 'g')) || []).length, 2);
  assert.doesNotMatch(page, /<input\b[^>]*type=["']password/);
  assert.doesNotMatch(page, /action=["']https?:/);
  assert.match(page, /Passwords and session tokens are handled by the account server/);
});
test('visual elements are local, responsive, reduced-motion-friendly', () => {
  assert.match(page, /id="network"/);
  assert.match(page, /class="launch-frame"/);
  assert.match(page, /CSS\/account\.css/);
  assert.match(page, /JS\/account\.js/);
  assert.match(css, /@media\(max-width:760px\)/);
  assert.match(css, /@media\(prefers-reduced-motion:reduce\)/);
  assert.match(script, /document\.hidden/);
  assert.match(script, /prefers-reduced-motion: reduce/);
  assert.doesNotMatch(script, /localStorage|sessionStorage|fetch\(|XMLHttpRequest|\.cookie/);
});
