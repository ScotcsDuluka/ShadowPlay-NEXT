'use strict';

// Standalone Gallery copy. Does not report success unless filesystem copy
// completes. Existing destination files are NEVER replaced.
const fs = require('fs');
const path = require('path');

const MEDIA_EXTENSIONS = new Set([
  '.avi', '.mkv', '.mov', '.mp4', '.mpeg', '.mpg', '.webm', '.wmv',
  '.bmp', '.gif', '.jpeg', '.jpg', '.png'
]);

function copy(options, resolveSource, callback) {
  if (!options || typeof options !== 'object' ||
      typeof options.source !== 'string' ||
      typeof options.destination !== 'string' ||
      options.source.length > 4096 || options.destination.length > 4096 ||
      !path.isAbsolute(options.destination)) {
    return callback({ statusCode: 400, codeText: 'GALLERY_INVALID_COPY_PATH' });
  }

  const destination = path.normalize(options.destination);
  if (!MEDIA_EXTENSIONS.has(path.extname(destination).toLowerCase())) {
    return callback({ statusCode: 400, codeText: 'GALLERY_UNSUPPORTED_DESTINATION' });
  }

  resolveSource(options.source, function (sourceError, media) {
    if (sourceError) return callback({
      statusCode: sourceError.statusCode || 404,
      codeText: 'GALLERY_SOURCE_UNAVAILABLE'
    });
    if (!media || !MEDIA_EXTENSIONS.has(path.extname(media.filename).toLowerCase())) {
      return callback({ statusCode: 400, codeText: 'GALLERY_UNSUPPORTED_SOURCE' });
    }
    fs.realpath(path.dirname(destination), function (dirError, realParent) {
      if (dirError) {
        return callback({ statusCode: dirError.code === 'ENOENT' ? 404 : 403,
          codeText: 'GALLERY_DESTINATION_DIRECTORY_UNAVAILABLE' });
      }
      const target = path.join(realParent, path.basename(destination));
      if (target.toLowerCase() === media.filename.toLowerCase()) {
        return callback({ statusCode: 409, codeText: 'GALLERY_SOURCE_EQUALS_DESTINATION' });
      }
      fs.copyFile(media.filename, target, fs.constants.COPYFILE_EXCL, function (copyError) {
        if (copyError) {
          return callback({
            statusCode: copyError.code === 'EEXIST' ? 409 : 500,
            codeText: copyError.code === 'EEXIST'
              ? 'GALLERY_DESTINATION_EXISTS' : 'GALLERY_COPY_FAILED'
          });
        }
        // Callback only after actual successful copy, never on a shim stub.
        callback(null, { copied: true });
      });
    });
  });
}

module.exports = { copy: copy };
