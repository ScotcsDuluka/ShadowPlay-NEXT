'use strict';
const fs=require('fs'),f=process.argv[2];if(!f)throw Error('usage: node inspect-intro-mp4.cjs file.mp4');
const b=fs.readFileSync(f);const matches=[];
const atoms=['avc1','avc3','hvc1','hev1','mp4a','vp09','av01','moov','mdat','mvhd','tkhd','stsd','ftyp'];
for(let i=0;i<b.length-4;i++){
  const s=b.toString('ascii',i,i+4);
  if(atoms.indexOf(s)>=0) matches.push(s+'@'+i);
}
console.log('size='+b.length,'atoms='+matches.slice(0,40).join(' '));
let pos=b.indexOf(Buffer.from('mvhd'));
if(pos!==-1){const v=b[pos+4],scale=b.readUInt32BE(pos+(v?24:16)),duration=v?Number(b.readBigUInt64BE(pos+28)):b.readUInt32BE(pos+20);
console.log('durationSec='+(duration/scale).toFixed(3)+' timescale='+scale)}
console.log('signature='+b.subarray(0,32).toString('hex'));
