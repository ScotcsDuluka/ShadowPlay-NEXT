'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const img = require('../shims/gallery-image-dimensions');

function check(bytes, expected) {
  assert.deepEqual(img.dimensions(bytes), expected);
}
test('PNG reads IHDR width and height', () => {
  const b = Buffer.alloc(24);
  Buffer.from([137,80,78,71,13,10,26,10]).copy(b);
  b.write('IHDR', 12, 'ascii');
  b.writeUInt32BE(1920, 16);
  b.writeUInt32BE(1080, 20);
  check(b, {width:1920, height:1080});
});
test('GIF reads logical screen dimensions', () => {
  const b = Buffer.alloc(10);
  b.write('GIF89a', 0, 'ascii');
  b.writeUInt16LE(320,6);b.writeUInt16LE(180,8);
  check(b,{width:320,height:180});
});
test('BMP supports negative-height top-down images', () => {
  const b=Buffer.alloc(40);
  b.write('BM',0,'ascii');b.writeUInt32LE(40,14);
  b.writeInt32LE(640,18);b.writeInt32LE(-480,22);
  check(b,{width:640,height:480});
});
test('JPEG scans start-of-frame segment without decoding pixel content', () => {
  const b = Buffer.from([0xff,0xd8,0xff,0xe0,0,4,0,0,
    0xff,0xc0,0,11,8,0x04,0x38,0x07,0x80,3,1,0x11,0,
    0xff,0xd9]);
  check(b,{width:1920,height:1080});
});
test('rejects invalid/zero-sized file headers', () => {
  check(Buffer.from('hello'), null);
  const b=Buffer.alloc(24);Buffer.from([137,80,78,71,13,10,26,10]).copy(b);
  b.write('IHDR',12);b.writeUInt32BE(0,16);b.writeUInt32BE(10,20);
  check(b,null);
});
test('real file read returns accurate image dimensions without edits', async t => {
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'nv-gallery-img-'));
  t.after(()=>fs.rmSync(root,{recursive:true,force:true}));
  const filepath=path.join(root,'shot.png');
  const b=Buffer.alloc(24);
  Buffer.from([137,80,78,71,13,10,26,10]).copy(b);b.write('IHDR',12);
  b.writeUInt32BE(1440,16);b.writeUInt32BE(900,20);
  fs.writeFileSync(filepath,b);
  const result=await new Promise(resolve=>
    img.read({file:filepath},(err,data)=>resolve({err,data})));
  assert.equal(result.err,null);
  assert.deepEqual(result.data,{width:1440,height:900});
  assert.deepEqual(fs.readFileSync(filepath),b,'image bytes never changed');
});
test('missing file or non-image path does not get false success', async () => {
  const bad=await new Promise(resolve=>img.read({file:'relative.jpg'},e=>resolve(e)));
  assert.equal(bad.statusCode,400);
  const missing=await new Promise(resolve=>img.read({file:path.join(os.tmpdir(),'doesnotexist-ngallerystill.jpg')},e=>resolve(e)));
  assert.equal(missing.statusCode,404);
});
