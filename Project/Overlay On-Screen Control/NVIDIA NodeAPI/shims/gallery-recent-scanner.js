'use strict';

// Bounded recursive scan for the Gallery "Recent" view. Never follows
// symlinks/reparse points or scans outside the configured recording root.
const fs = require('fs');
const path = require('path');

const EXTENSIONS = new Set([
  '.avi', '.mkv', '.mov', '.mp4', '.mpeg', '.mpg', '.webm', '.wmv',
  '.bmp', '.gif', '.jpeg', '.jpg', '.png'
]);
const MAX_DEPTH = 5;
const MAX_FOLDERS = 128;
const MAX_MEDIA = 4096;
const STAT_BATCH = 16;

function recent(directory, count, callback) {
  if (typeof directory !== 'string' || !directory.trim()) {
    callback(new Error('Gallery recording folder is not configured'));
    return;
  }
  const root = path.resolve(directory.trim());
  const requested = Number.parseInt(count, 10);
  const limit = Number.isFinite(requested)
    ? Math.min(Math.max(requested, 0), 100) : 0;
  if (!limit) { callback(null, { files: [] }); return; }

  let finished = false;
  function finish(error, files) {
    if (finished) return;
    finished = true;
    callback(error, files);
  }
  const queue = [{ folder: root, depth: 0 }];
  const candidates = [];
  let folderCount = 0;

  function nextFolder() {
    if (!queue.length || folderCount >= MAX_FOLDERS ||
        candidates.length >= MAX_MEDIA) {
      return collectStats();
    }
    const current = queue.shift();
    folderCount++;
    fs.readdir(current.folder, { withFileTypes: true }, function (error, entries) {
      if (error) {
        // If the recording root cannot be read, report the failure; a
        // disappearing or inaccessible nested folder must not blank Gallery.
        if (current.depth === 0) return finish(error);
        return nextFolder();
      }
      entries.forEach(function (entry) {
        if (entry.isSymbolicLink()) return;
        if (entry.isDirectory()) {
          if (current.depth < MAX_DEPTH &&
              queue.length + folderCount < MAX_FOLDERS &&
              entry.name[0] !== '.' && entry.name[0] !== '$') {
            queue.push({ folder: path.join(current.folder, entry.name),
              depth: current.depth + 1 });
          }
        } else if (entry.isFile() && candidates.length < MAX_MEDIA &&
            EXTENSIONS.has(path.extname(entry.name).toLowerCase())) {
          candidates.push(path.join(current.folder, entry.name));
        }
      });
      nextFolder();
    });
  }

  function collectStats() {
    const items = [];
    let index = 0;
    function batch() {
      const chunk = candidates.slice(index, index + STAT_BATCH);
      index += chunk.length;
      if (!chunk.length) {
        items.sort(function (a, b) {
          return b.time - a.time || a.file.localeCompare(b.file);
        });
        finish(null, { files: items.slice(0, limit).map(x => x.file) });
        return;
      }
      let remaining = chunk.length;
      chunk.forEach(function (file) {
        fs.stat(file, function (error, stat) {
          if (!error && stat.isFile()) {
            items.push({ file: file, time: stat.mtimeMs });
          }
          if (--remaining === 0) batch();
        });
      });
    }
    batch();
  }
  nextFolder();
}

module.exports = { recent: recent };
