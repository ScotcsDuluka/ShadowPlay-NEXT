'use strict';
// OSC locale parity audit/fallback filler. Run: node tools/locale-coverage.cjs [--fix]
// Never replaces existing translations. Generated English values are fallback text,
// NOT an assertion that the target language has been translated.
const fs = require('node:fs');
const path = require('node:path');
const root = path.join(__dirname, '..', 'osc', 'l10n');
const files = fs.readdirSync(root).filter(n => /^[a-z]{2}-[A-Z]{2,3}\.json$/.test(n)).sort();
const fix = process.argv.includes('--fix');
const english = JSON.parse(fs.readFileSync(path.join(root, 'en-US.json'), 'utf8').replace(/^\uFEFF/, ''));
const keys = Object.keys(english);
const keysAll = new Set(keys);
const previousManifestPath = path.join(__dirname, 'locale-fallbacks.json');
let previous = {};
if (fs.existsSync(previousManifestPath)) {
  previous = JSON.parse(fs.readFileSync(previousManifestPath, 'utf8')).englishFallbacks || {};
}
const report = {}, filled = {};
let errors = 0, added = 0, missingTotal = 0, untranslated = 0;
for (const name of files) {
  const locale = name.slice(0, -5);
  const filepath = path.join(root, name);
  const raw = fs.readFileSync(filepath, 'utf8');
  const bom = raw.startsWith('\uFEFF') ? '\uFEFF' : '';
  const clean = raw.replace(/^\uFEFF/, '');
  const data = JSON.parse(clean);
  const missing = keys.filter(k => !Object.prototype.hasOwnProperty.call(data, k));
  const invalid = keys.filter(k => Object.prototype.hasOwnProperty.call(data, k) && (typeof data[k] !== 'string' || !data[k].trim()));
  const extra = Object.keys(data).filter(k => !keysAll.has(k));
  // Flat OSC dictionaries: report duplicate top-level declarations before JSON.parse silently replaces values.
  const declared = [...clean.matchAll(/^\s*"((?:[^"\\]|\\.)*)"\s*:/gm)].map(m => JSON.parse('"' + m[1] + '"'));
  const seen = new Set(), duplicates = declared.filter(k => seen.has(k) || !seen.add(k));
  let fallback = Array.isArray(previous[locale]) ? previous[locale].filter(k => data[k] === english[k]) : [];
  if (fix && missing.length) {
    const lines = Object.keys(english).filter(k => missing.includes(k)).map(k => '  ' + JSON.stringify(k) + ': ' + JSON.stringify(english[k]) + ',');
    const nl = clean.includes('\r\n') ? '\r\n' : '\n';
    const index = clean.indexOf(nl);
    if (index < 0 || !clean.slice(0, index).includes('{')) throw new Error('Invalid locale header: ' + name);
    // Insert immediately after the initial opening brace. All inserted entries have trailing commas,
    // and existing content starts with at least one property.
    const updated = clean.slice(0, index + nl.length) + lines.join(nl) + nl + clean.slice(index + nl.length);
    JSON.parse(updated);
    fs.writeFileSync(filepath, bom + updated, 'utf8');
    added += missing.length;
    filled[locale] = missing;
    fallback = [...new Set([...fallback, ...missing])];
  }
  if (!fix && missing.length) missingTotal += missing.length;
  const notTranslated = keys.filter(k => k !== 'meta.languageName' && data[k] === english[k] && locale !== 'en-US');
  untranslated += notTranslated.length;
  report[locale] = {originalMissing: missing.length, actualMissing: fix ? 0 : missing.length,
    invalid:invalid.length, duplicates:[...new Set(duplicates)], extra:extra.length,
    englishFallbacks:fallback.length, identicalToEnglishBeforeFill:notTranslated.length};
  if (invalid.length || duplicates.length || extra.length || (!fix && missing.length)) errors++;
  console.log(locale + ' missing=' + missing.length + ' invalid=' + invalid.length +
    ' dup=' + duplicates.length + ' extra=' + extra.length + ' fallbackAdded=' + (fix ? missing.length : 0));
}
if (files.length !== 28) {console.error('Expected 28 locales; got ' + files.length); errors++;}
if (fix) {
  const newFallbacks = {};
  for (const locale of files.map(n => n.slice(0,-5))) {
    const data = JSON.parse(fs.readFileSync(path.join(root, locale + '.json'), 'utf8').replace(/^\uFEFF/, ''));
    // Track only fallback entries this tool actually inserted, not brand names
    // or inherited English strings that existed before the parity fix.
    newFallbacks[locale] = [...new Set([
      ...(previous[locale] || []).filter(k => data[k] === english[k]),
      ...(filled[locale] || [])
    ])];
  }
  fs.writeFileSync(previousManifestPath, JSON.stringify({
    description:'English text copied only for keys absent from a locale. These are NOT translations.',
    reference:'en-US', englishFallbacks:newFallbacks
  }, null, 2) + '\n');
}
console.log('SUMMARY locales=' + files.length + ' englishKeys=' + keys.length +
  ' added=' + added + ' remainingMissing=' + missingTotal + ' errors=' + errors);
if (errors) process.exitCode = 1;
