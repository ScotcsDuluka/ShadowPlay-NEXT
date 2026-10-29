// OSC source-first live preview sync. No file outside OSC is touched.
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const cp = require('node:child_process');
const crypto = require('node:crypto');

const NODE_ROOT = path.resolve(__dirname, '..');
const REPO = path.resolve(NODE_ROOT, '..', '..', '..');
const SOURCE = path.join(NODE_ROOT, 'osc');
const RUNTIME = path.join(REPO, 'build', 'NVIDIA ShadowPlay', 'Overlay On-Screen Control', 'NVIDIA NodeAPI', 'osc');
const SESSION_BASE = path.join(REPO, 'build_test', 'OSC-Live-Preview');
const ROOT_PREFIX = 'Project/Overlay On-Screen Control/NVIDIA NodeAPI/osc/';
const FILE_PATTERN = /\.(?:html|js|css|json|png|svg|ico|ttf|woff2?|jpe?g|webp)$/i;
const EXCLUDED = new Set(['config.js', 'user-config.js', 'launcher-language.json']);
function assert(ok, msg) { if (!ok) throw new Error(msg); }
function hash(file) {
  if (!fs.existsSync(file)) return null;
  return hashBytes(fs.readFileSync(file));
}
function hashBytes(bytes) { return crypto.createHash('sha256').update(bytes).digest('hex').toUpperCase(); }
function safeRelative(file) {
  assert(typeof file === 'string' && file.length > 0, 'Empty relative path');
  assert(!path.isAbsolute(file) && !/^[a-z]:/i.test(file) &&
    !file.includes('\\') && !/[\x00-\x1f\x7f]/.test(file) &&
    !file.split('/').some(x => !x || x === '.' || x === '..' || x.includes(':')),
    'Unsafe file path: ' + file);
  return file;
}
function subpath(root, rel) {
  safeRelative(rel);
  const p = path.resolve(root, ...rel.split('/'));
  assert(p.startsWith(path.resolve(root) + path.sep), 'Outside root: ' + rel);
  return p;
}
function assertNoSymlinks(root, rel) {
  const parts = rel.split('/');
  let next = root;
  if (fs.existsSync(root)) assert(!fs.lstatSync(root).isSymbolicLink(), 'Root is symlink: ' + root);
  for (const item of parts) {
    next = path.join(next, item);
    if (fs.existsSync(next)) assert(!fs.lstatSync(next).isSymbolicLink(), 'Refusing symlink: ' + next);
  }
}
function git(args, repo = REPO) {
  return cp.execFileSync('git', ['-C', repo].concat(args), {
    encoding: 'utf8', maxBuffer: 8 * 1024 * 1024, windowsHide: true
  }).trim();
}
function allowedFrontend(file) {
  safeRelative(file);
  assert(FILE_PATTERN.test(file) && !EXCLUDED.has(file.toLowerCase()) &&
    !/(?:^|\/)[^/]*\.bak(?:[-.]|$)/i.test(file) &&
    !/(?:^|\/)[^/]*\.tmp$/i.test(file), 'Not an allowed OSC frontend file: ' + file);
  return file;
}
function selectedFiles(files) {
  assert(Array.isArray(files) && files.length > 0, 'Select at least one OSC frontend file');
  files.forEach(allowedFrontend);
  assert(new Set(files).size === files.length, 'Duplicate selected file');
  return files.slice().sort();
}
function sources() {
  const raw = cp.execFileSync('git',
    ['-C', REPO, 'ls-files', '-z', '--', ROOT_PREFIX.slice(0, -1)],
    { maxBuffer: 8 * 1024 * 1024, windowsHide: true }).toString('utf8');
  const files = raw.split('\0').filter(Boolean).filter(x => x.startsWith(ROOT_PREFIX))
    .map(x => x.slice(ROOT_PREFIX.length).replace(/\\/g, '/'))
    .filter(x => FILE_PATTERN.test(x) && !EXCLUDED.has(x) &&
      !/(?:^|\/)[^/]*\.bak(?:[-.]|$)/i.test(x) &&
      !/(?:^|\/)[^/]*\.tmp$/i.test(x));
  assert(files.length >= 70, 'Too few tracked OSC files; refusing to mirror');
  const unique = [...new Set(files)].sort();
  for (const file of unique) {
    safeRelative(file);
    assertNoSymlinks(SOURCE, file);
    assert(fs.existsSync(subpath(SOURCE, file)), 'Tracked source missing: ' + file);
  }
  return unique;
}
function plan(options = {}) {
  const src = options.source || SOURCE, dest = options.runtime || RUNTIME;
  assert(fs.existsSync(src) && fs.existsSync(dest), 'Missing Source or Runtime OSC folder');
  const files = options.files === undefined ? sources() : selectedFiles(options.files);
  const changed = [], same = [];
  for (const file of files) {
    assertNoSymlinks(src, file);
    assertNoSymlinks(dest, file);
    const from = subpath(src, file), to = subpath(dest, file);
    const sourceHash = hash(from), beforeHash = hash(to);
    assert(sourceHash, 'Missing source: ' + file);
    const item = { file, sourceHash, beforeHash, existed: beforeHash !== null };
    (sourceHash === beforeHash ? same : changed).push(item);
  }
  return { source: src, runtime: dest, changed, unchanged: same.length, tracked: files.length,
    sourceHashes: Object.fromEntries(changed.concat(same).map(item => [item.file, item.sourceHash])) };
}
function atomicJson(file, data) {
  const temp = file + '.writing-' + process.pid;
  fs.writeFileSync(temp, JSON.stringify(data, null, 2) + '\n');
  // Windows security scanners may briefly hold the replaced manifest open.
  // Retry the rename without ever deleting the prior verified manifest.
  for (let retry = 0; retry < 15; retry++) {
    try { fs.renameSync(temp, file); return; }
    catch (error) {
      if (!['EPERM', 'EBUSY', 'EACCES'].includes(error.code) || retry === 14) throw error;
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0,
        Math.min(300, 30 * (retry + 1)));
    }
  }
}
function lockRun(base, fn) {
  fs.mkdirSync(base, { recursive: true });
  const lock = path.join(base, '.preview-sync.lock');
  let fd;
  try { fd = fs.openSync(lock, 'wx'); }
  catch (e) { throw new Error('Another Preview operation holds ' + lock + ' (' + e.code + ')'); }
  try {
    fs.writeFileSync(fd, String(process.pid));
    return fn();
  } finally {
    fs.closeSync(fd);
    fs.unlinkSync(lock);
  }
}
function projectPreflight(files, options = {}) {
  const repo = options.repo || REPO, src = options.source || SOURCE;
  assert(path.resolve(src) === path.resolve(repo, ...ROOT_PREFIX.split('/')),
    'Source must be the repository Project OSC folder');
  assert(git(['branch', '--show-current'], repo) === 'Stable', 'Refusing non-Stable branch');
  const selected = files === undefined ? undefined : selectedFiles(files);
  const paths = selected ? selected.map(file => ':(literal)' + ROOT_PREFIX + file) : [ROOT_PREFIX.slice(0, -1)];
  const dirty = git(['status', '--porcelain', '--untracked-files=all', '--'].concat(paths), repo);
  assert(!dirty, 'OSC sources changed but not committed:\n' + dirty);
  const commit = git(['rev-parse', 'HEAD'], repo);
  if (selected) verifyCommittedSources(selected, commit, options);
  return commit;
}
function verifyCommittedSources(files, commit, options = {}, planned) {
  const repo = options.repo || REPO, src = options.source || SOURCE;
  for (const file of selectedFiles(files)) {
    assertNoSymlinks(src, file);
    const absolute = subpath(src, file), tracked = ROOT_PREFIX + file;
    assert(fs.existsSync(absolute) && fs.statSync(absolute).isFile(), 'Missing source: ' + file);
    const before = hash(absolute);
    if (planned) assert(before === planned.get(file), 'Source edited since planning: ' + file);
    let committed;
    try { committed = git(['rev-parse', '--verify', commit + ':' + tracked], repo); }
    catch (_) { throw new Error('Selected file is not committed: ' + file); }
    // --path applies the same Git clean filters/EOL normalization as git add.
    // Raw deployment hashes remain byte-for-byte hashes of the working file.
    const actual = git(['hash-object', '--path=' + tracked, '--', absolute], repo);
    assert(actual === committed, 'Source does not match committed HEAD: ' + file);
    assert(hash(absolute) === before, 'Source edited during verification: ' + file);
  }
}
function sessionId() {
  const d = new Date().toISOString().replace(/[-:]/g, '').replace(/\..*$/, '').replace('T', '-');
  return d + '-' + crypto.randomBytes(4).toString('hex');
}
function deploy(options = {}) {
  const src = options.source || SOURCE, dest = options.runtime || RUNTIME;
  const base = options.base || SESSION_BASE;
  if (options.skipGit) assert(path.resolve(src) !== path.resolve(SOURCE) && path.resolve(dest) !== path.resolve(RUNTIME),
    'skipGit is permitted only for isolated test fixtures');
  return lockRun(base, () => {
    const commit = options.skipGit ? 'TEST-FIXTURE' : projectPreflight(options.files, options);
    const p = plan({ source: src, runtime: dest, files: options.files });
    if (!options.skipGit) {
      // Also check unchanged selected files: an edit must not be hidden merely
      // because its uncommitted bytes already happen to exist in Runtime.
      const files = options.files === undefined ? sources() : selectedFiles(options.files);
      verifyCommittedSources(files, commit, options, new Map(Object.entries(p.sourceHashes)));
    }
    if (p.changed.length === 0) return { result: 'NO_CHANGES', commit, plan: p };
    const id = sessionId();
    const folder = path.join(base, 'sessions', id);
    assert(!fs.existsSync(folder), 'Session collision');
    fs.mkdirSync(path.join(folder, 'files'), { recursive: true });
    const manifest = {
      schema: 1, kind: 'osc-frontend-only', session: id,
      selection: options.files === undefined ? 'all-tracked-frontend' : selectedFiles(options.files),
      createdAt: new Date().toISOString(), branch: 'Stable',
      commit, source: path.resolve(src), runtime: path.resolve(dest),
      status: 'preparing', files: p.changed.map(x => Object.assign({}, x, { phase: 'pending' }))
    };
    const manifestFile = path.join(folder, 'manifest.json');
    const save = () => atomicJson(manifestFile, manifest);
    // Back up everything before writing even the first file.
    for (const x of manifest.files) {
      const target = subpath(dest, x.file);
      if (x.existed) {
        assert(hash(target) === x.beforeHash, 'Runtime changed since planning: ' + x.file);
        const backup = subpath(path.join(folder, 'files'), x.file);
        fs.mkdirSync(path.dirname(backup), { recursive: true });
        fs.copyFileSync(target, backup);
        assert(hash(backup) === x.beforeHash, 'Backup SHA mismatch: ' + x.file);
      }
    }
    manifest.status = 'prepared'; save();
    try {
      manifest.status = 'applying'; save();
      for (const x of manifest.files) {
        assertNoSymlinks(src, x.file); assertNoSymlinks(dest, x.file);
        const from = subpath(src, x.file), to = subpath(dest, x.file);
        const bytes = fs.readFileSync(from);
        assert(hashBytes(bytes) === x.sourceHash, 'Source edited during deploy: ' + x.file);
        assert(hash(to) === x.beforeHash, 'Runtime edited during deploy: ' + x.file);
        x.phase = 'copying'; save();
        fs.mkdirSync(path.dirname(to), { recursive: true });
        fs.writeFileSync(to, bytes);
        assert(hash(to) === x.sourceHash, 'Deploy verification failed: ' + x.file);
        x.phase = 'applied'; save();
      }
      manifest.status = 'applied'; manifest.appliedAt = new Date().toISOString(); save();
    } catch (e) {
      manifest.status = 'interrupted'; manifest.error = String(e.message); save();
      throw new Error('Preview deploy interrupted (backup intact, rollback via --rollback ' + id + '): ' + e.message);
    }
    return { result: 'APPLIED', session: id, commit, changed: p.changed.length, unchanged: p.unchanged, folder };
  });
}
function listSessions(base = SESSION_BASE) {
  const dir = path.join(base, 'sessions');
  if (!fs.existsSync(dir)) return [];
  return fs.readdirSync(dir).filter(x => /^[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$/.test(x))
    .map(x => {
      const mf = path.join(dir, x, 'manifest.json');
      if (!fs.existsSync(mf)) return null;
      const m = JSON.parse(fs.readFileSync(mf, 'utf8'));
      return { session: m.session, status: m.status, commit: m.commit,
        changed: m.files.length, createdAt: m.createdAt };
    }).filter(Boolean).reverse();
}
function rollback(id, options = {}) {
  assert(/^[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$/.test(id || ''), 'Invalid session ID');
  const base = options.base || SESSION_BASE;
  const file = path.join(base, 'sessions', id, 'manifest.json');
  assert(fs.existsSync(file), 'Unknown session ' + id);
  return lockRun(base, () => {
    const manifest = JSON.parse(fs.readFileSync(file, 'utf8'));
    assert(manifest.kind === 'osc-frontend-only' && manifest.session === id, 'Invalid manifest');
    assert(['applied', 'applying', 'interrupted'].includes(manifest.status), 'Session not active: ' + manifest.status);
    const runtime = options.runtime || RUNTIME;
    assert(path.resolve(manifest.runtime).toLowerCase() === path.resolve(runtime).toLowerCase(),
      'Refusing rollback into different Runtime');
    // Strong drift check across all files BEFORE restoring anything.
    const targetItems = manifest.files.filter(x => x.phase === 'applied' || x.phase === 'copying');
    for (const x of targetItems) {
      safeRelative(x.file);assertNoSymlinks(runtime, x.file);
      const actual = hash(subpath(runtime, x.file));
      assert(actual === x.sourceHash || actual === x.beforeHash,
        'Preview file changed externally: ' + x.file + '; no rollback occurred');
      if (x.existed) {
        const backup = subpath(path.join(base, 'sessions', id, 'files'), x.file);
        assert(hash(backup) === x.beforeHash, 'Backup corrupted: ' + x.file);
      }
    }
    manifest.status = 'rolling-back'; atomicJson(file, manifest);
    for (const x of targetItems.slice().reverse()) {
      const target = subpath(runtime, x.file), actual = hash(target);
      if (actual === x.beforeHash) continue;
      if (x.existed) {
        const backup = subpath(path.join(base, 'sessions', id, 'files'), x.file);
        fs.copyFileSync(backup, target);
      } else {
        fs.unlinkSync(target);
      }
      assert(hash(target) === x.beforeHash, 'Rollback failed SHA: ' + x.file);
      x.phase = 'restored';
      atomicJson(file, manifest);
    }
    manifest.status = 'rolled-back'; manifest.rolledBackAt = new Date().toISOString();
    atomicJson(file, manifest);
    return { result: 'ROLLED_BACK', session: id, files: targetItems.length };
  });
}
function runCLI(argv = process.argv.slice(2)) {
  const usage = 'Usage: node osc-live-preview.cjs --plan|--apply [--files OSC_RELATIVE_FILE ... | --files-json FILE.json] | --status | --rollback SESSION_ID';
  assert(argv.length > 0, usage);
  const cmd = argv[0];
  function selection() {
    if (argv.length === 1) return undefined;
    if (argv[1] === '--files') return selectedFiles(argv.slice(2));
    if (argv[1] === '--files-json' && argv.length === 3)
      return selectedFiles(JSON.parse(fs.readFileSync(argv[2], 'utf8')));
    throw new Error(usage);
  }
  if (cmd === '--plan') {
    const files = selection(), commit = projectPreflight(files), p = plan({ files });
    if (files) verifyCommittedSources(files, commit, {}, new Map(Object.entries(p.sourceHashes)));
    console.log(JSON.stringify({ mode: 'PLAN', branch: 'Stable', commit, total: p.tracked,
      selection: files || 'all-tracked-frontend',
      unchanged: p.unchanged, changed: p.changed.map(x => ({ file: x.file, existed: x.existed,
        beforeSHA256: x.beforeHash, sourceSHA256: x.sourceHash })),
      note: 'No changes made. Use --apply to backup and mirror these files.' }, null, 2));
  } else if (cmd === '--apply') {
    console.log(JSON.stringify(deploy({ files: selection() }), null, 2));
  } else if (cmd === '--status') {
    assert(argv.length === 1, usage);
    console.log(JSON.stringify({ branch: git(['branch', '--show-current']),
      commit: git(['rev-parse', 'HEAD']), runtime: RUNTIME,
      sessions: listSessions(), note: 'Status does not alter files.' }, null, 2));
  } else if (cmd === '--rollback') {
    assert(argv.length === 2, usage);
    console.log(JSON.stringify(rollback(argv[1]), null, 2));
  } else throw new Error(usage);
}
if (require.main === module) {
  try { runCLI(); } catch (error) { console.error('OSC_PREVIEW_ERROR: ' + error.message); process.exitCode = 1; }
}
module.exports = { hash, safeRelative, selectedFiles, projectPreflight, verifyCommittedSources,
  plan, deploy, rollback, listSessions, runCLI,
  defaults: { REPO, SOURCE, RUNTIME, SESSION_BASE, ROOT_PREFIX } };
