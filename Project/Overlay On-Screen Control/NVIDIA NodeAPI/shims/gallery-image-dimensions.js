'use strict';

// Bounded, read-only image header inspection. No external codecs or image
// decompression. Does not follow remote UNC destinations.
const fs = require('fs');
const path = require('path');
const SUPPORTED = new Set(['.png', '.jpg', '.jpeg', '.gif', '.bmp']);

function dimensions(buffer) {
  if (!Buffer.isBuffer(buffer)) return null;
  let width;
  let height;
  if (buffer.length >= 24 &&
      buffer.subarray(0, 8).equals(Buffer.from([137,80,78,71,13,10,26,10])) &&
      buffer.toString('ascii', 12, 16) === 'IHDR') {
    width = buffer.readUInt32BE(16);
    height = buffer.readUInt32BE(20);
  } else if (buffer.length >= 10 &&
      (buffer.toString('ascii', 0, 6) === 'GIF87a' ||
       buffer.toString('ascii', 0, 6) === 'GIF89a')) {
    width = buffer.readUInt16LE(6);
    height = buffer.readUInt16LE(8);
  } else if (buffer.length >= 26 &&
      buffer.toString('ascii', 0, 2) === 'BM') {
    const dib = buffer.readUInt32LE(14);
    if (dib >= 40) {
      width = Math.abs(buffer.readInt32LE(18));
      height = Math.abs(buffer.readInt32LE(22));
    } else if (dib === 12) {
      width = buffer.readUInt16LE(18);
      height = buffer.readUInt16LE(20);
    }
  } else if (buffer.length >= 4 && buffer[0] === 0xff && buffer[1] === 0xd8) {
    let offset = 2;
    while (offset + 4 <= buffer.length) {
      if (buffer[offset] !== 0xff) { offset++; continue; }
      while (offset < buffer.length && buffer[offset] === 0xff) offset++;
      if (offset >= buffer.length) break;
      const marker = buffer[offset++];
      if (marker === 0xd9 || marker === 0xda) break;
      if (marker === 0x01 || (marker >= 0xd0 && marker <= 0xd8)) continue;
      if (offset + 2 > buffer.length) break;
      const length = buffer.readUInt16BE(offset);
      if (length < 2 || offset + length > buffer.length) break;
      if ((marker >= 0xc0 && marker <= 0xcf &&
          marker !== 0xc4 && marker !== 0xc8 && marker !== 0xcc) &&
          length >= 7) {
        height = buffer.readUInt16BE(offset + 3);
        width = buffer.readUInt16BE(offset + 5);
        break;
      }
      offset += length;
    }
  }
  if (!Number.isInteger(width) || !Number.isInteger(height) ||
      width < 1 || height < 1 || width > 100000 || height > 100000) return null;
  return { width, height };
}

function read(options, callback) {
  const filename = options && options.file;
  if (typeof filename !== 'string' || !filename.trim() ||
      filename.length > 4096 || !path.isAbsolute(filename) ||
      filename.startsWith('\\\\') || !SUPPORTED.has(path.extname(filename).toLowerCase())) {
    return callback({ statusCode: 400, codeText: 'GALLERY_IMAGE_PATH_INVALID' });
  }

  fs.open(filename, 'r', function (openError, fd) {
    if (openError) return callback({
      statusCode: openError.code === 'ENOENT' ? 404 : 403,
      codeText: 'GALLERY_IMAGE_UNAVAILABLE'
    });
    fs.fstat(fd, function (statError, stats) {
      if (statError || !stats.isFile()) {
        return fs.close(fd, function () {
          callback({ statusCode: 400, codeText: 'GALLERY_IMAGE_NOT_FILE' });
        });
      }
      const buffer = Buffer.alloc(Math.min(stats.size, 256 * 1024));
      fs.read(fd, buffer, 0, buffer.length, 0, function (readError, count) {
        fs.close(fd, function () {
          if (readError) return callback({ statusCode: 500,
            codeText: 'GALLERY_IMAGE_READ_FAILED' });
          const data = dimensions(buffer.subarray(0, count));
          if (!data) return callback({ statusCode: 422,
            codeText: 'GALLERY_IMAGE_UNSUPPORTED_HEADER' });
          callback(null, data);
        });
      });
    });
  });
}

module.exports = { dimensions, read };
