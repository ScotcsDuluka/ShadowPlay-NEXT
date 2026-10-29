'use strict';
const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
const src=fs.readFileSync(path.join(__dirname,'../osc/Module/Overlay/Background/osc-bg-webgl.js'),'utf8');
function load(){const window={};vm.runInNewContext(src,{window,Float32Array});return window.__dulukaBgWebgl;}
function glMock(){
 const calls=[];
 const gl={calls,VERTEX_SHADER:35633,FRAGMENT_SHADER:35632,COMPILE_STATUS:35713,LINK_STATUS:35714,
  ARRAY_BUFFER:34962,DYNAMIC_DRAW:35048,FLOAT:5126,BLEND:3042,DEPTH_TEST:2929,
  SRC_ALPHA:770,ONE_MINUS_SRC_ALPHA:771,COLOR_BUFFER_BIT:16384,LINES:1,POINTS:0};
 for(const method of ['shaderSource','compileShader','deleteShader','attachShader','linkProgram','useProgram','enable','disable','blendFunc','viewport','clearColor','clear','bindBuffer','bufferData','uniform1f','enableVertexAttribArray','vertexAttribPointer'])
  gl[method]=(...args)=>calls.push({method,args});
 gl.createShader=()=>({});gl.createProgram=()=>({});gl.createBuffer=()=>({});
 gl.getShaderParameter=()=>true;gl.getProgramParameter=()=>true;
 gl.getAttribLocation=(program,name)=>name==='aPos'?0:name==='aSize'?1:2;
 gl.getUniformLocation=()=>({});gl.isContextLost=()=>false;
 gl.drawArrays=(mode,first,count)=>calls.push({method:'drawArrays',args:[mode,first,count]});
 return gl;
}
test('WebGL unavailable gracefully keeps Canvas 2D fallback possible',()=>{
 const creator=load();
 assert.equal(creator.create({getContext:()=>null}),null);
 assert.equal(creator.create({getContext:()=>{throw Error('disabled');}}),null);
});
test('GPU background renderer draws particles and links without changing main UI',()=>{
 const gl=glMock(),renderer=load().create({width:500,height:300,getContext:()=>gl});
 assert(renderer&&renderer.healthy());
 renderer.resize(500,300);
 renderer.draw([{x:20,y:50,size:1.5,opacity:.5},{x:60,y:70,size:2,opacity:1}],500,300,true);
 const calls=gl.calls.filter(x=>x.method==='drawArrays');
 assert.deepEqual(calls.map(x=>x.args[0]),[gl.LINES,gl.POINTS]);
 assert.deepEqual(calls.map(x=>x.args[2]),[2,2]);
});
test('disabling links renders points only',()=>{
 const gl=glMock(),renderer=load().create({width:100,height:100,getContext:()=>gl});
 renderer.draw([{x:20,y:40,size:1,opacity:.7}],100,100,false);
 const draws=gl.calls.filter(x=>x.method==='drawArrays');
 assert.equal(draws.length,1);assert.equal(draws[0].args[0],gl.POINTS);
});
