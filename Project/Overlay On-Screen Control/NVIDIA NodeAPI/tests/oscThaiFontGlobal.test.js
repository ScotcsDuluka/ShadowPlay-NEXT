'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const root = path.join(__dirname, '..', 'osc');
const stylesheet = fs.readFileSync(path.join(root, 'mods', 'osc-thai-font.css'), 'utf8');
const index = fs.readFileSync(path.join(root, 'index.html'), 'utf8');

test('Thai font covers the entire OSC document rather than only selected screens', () => {
  assert.match(stylesheet, /@font-face\s*\{[\s\S]*?font-family:\s*'MiSansThaiOSC'/);
  assert.match(stylesheet, /src:\s*url\('\/MiSansThai\.ttf'\)/);
  assert.match(stylesheet, /html\[lang="th"\]\s+body\s+\*:not\(md-icon\)/);
  assert.match(stylesheet, /font-family:\s*'MiSansThaiOSC',[^;]+!important/);
  assert.match(index, /osc-thai-font\.css\?v=20261022-global-text-icons-safe/);
  assert.ok(fs.statSync(path.join(root, 'MiSansThai.ttf')).size > 10000);
});

test('NVIDIA icon glyph targets are excluded from global Thai font rule', () => {
  for (const exclusion of [':not(md-icon)', ':not(.share-icon)', ':not(.nv-flex-icon)', ':not([class^="icon-"])', ':not([class*=" icon-"])']) {
    assert.ok(stylesheet.includes(exclusion), 'Missing icon exclusion '+exclusion);
  }
  assert.doesNotMatch(stylesheet, /font-family:\s*'nvgcshare'\s*!important/i);
});

test('Thai override deactivates for non-Thai locales', () => {
  const rule = stylesheet.slice(stylesheet.indexOf('html[lang="th"] body'));
  assert.ok(rule.includes('html[lang="th"]'));
  assert.ok(!/html\s+body\s+\*\s*\{\s*font-family/.test(rule));
});
