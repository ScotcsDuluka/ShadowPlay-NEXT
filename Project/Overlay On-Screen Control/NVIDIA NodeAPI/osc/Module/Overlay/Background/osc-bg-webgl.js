/* Optional WebGL particle renderer. Canvas 2D remains the unchanged default. */
(function (window) {
  'use strict';
  function compile(gl, type, source) {
    var shader = gl.createShader(type);
    gl.shaderSource(shader, source);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
      gl.deleteShader(shader); return null;
    }
    return shader;
  }
  function create(canvas) {
    var gl;
    try { gl = canvas.getContext('webgl', {alpha:true, antialias:true, depth:false, stencil:false}); }
    catch (ignore) { return null; }
    if (!gl) return null;
    var vs = compile(gl, gl.VERTEX_SHADER,
      'attribute vec2 aPos;attribute float aSize;attribute float aOpacity;' +
      'varying float vOpacity;void main(){gl_Position=vec4(aPos,0.0,1.0);' +
      'gl_PointSize=aSize;vOpacity=aOpacity;}');
    var fs = compile(gl, gl.FRAGMENT_SHADER,
      'precision mediump float;varying float vOpacity;uniform float uPoints;' +
      'void main(){if(uPoints>0.5 && distance(gl_PointCoord,vec2(0.5))>0.5)discard;' +
      'gl_FragColor=vec4(0.462745,0.72549,0.0,vOpacity);}');
    if (!vs || !fs) return null;
    var program = gl.createProgram();
    gl.attachShader(program,vs);gl.attachShader(program,fs);gl.linkProgram(program);
    if (!gl.getProgramParameter(program,gl.LINK_STATUS)) return null;
    var buffer = gl.createBuffer();
    var pos=gl.getAttribLocation(program,'aPos');
    var size=gl.getAttribLocation(program,'aSize');
    var alpha=gl.getAttribLocation(program,'aOpacity');
    var points=gl.getUniformLocation(program,'uPoints');
    if (pos<0 || size<0 || alpha<0 || points===null) return null;
    gl.useProgram(program);
    gl.enable(gl.BLEND);
    gl.blendFunc(gl.SRC_ALPHA,gl.ONE_MINUS_SRC_ALPHA);
    gl.disable(gl.DEPTH_TEST);
    function drawItems(vertices, primitive, isPoints) {
      if (!vertices.length) return;
      gl.bufferData(gl.ARRAY_BUFFER,new Float32Array(vertices),gl.DYNAMIC_DRAW);
      gl.uniform1f(points,isPoints?1:0);
      gl.drawArrays(primitive,0,vertices.length/4);
    }
    function draw(particles, width, height, links) {
      gl.viewport(0,0,canvas.width,canvas.height);
      gl.clearColor(0,0,0,0);gl.clear(gl.COLOR_BUFFER_BIT);
      gl.useProgram(program);gl.bindBuffer(gl.ARRAY_BUFFER,buffer);
      gl.enableVertexAttribArray(pos);gl.enableVertexAttribArray(size);gl.enableVertexAttribArray(alpha);
      gl.vertexAttribPointer(pos,2,gl.FLOAT,false,16,0);
      gl.vertexAttribPointer(size,1,gl.FLOAT,false,16,8);
      gl.vertexAttribPointer(alpha,1,gl.FLOAT,false,16,12);
      function normX(x){return x*2/width-1;}
      function normY(y){return 1-y*2/height;}
      var lines=[],dots=[];
      for(var i=0;i<particles.length;i++){
        var p=particles[i];
        dots.push(normX(p.x),normY(p.y),Math.max(2,p.size*2),Math.min(1,p.opacity));
      }
      if(links)for(var a=0;a<particles.length;a++)for(var b=a+1;b<particles.length;b++){
        var x=particles[a],y=particles[b],dx=x.x-y.x,dy=x.y-y.y;
        var distance=Math.sqrt(dx*dx+dy*dy);
        if(distance>=110)continue;
        var opacity=(110-distance)/120*.95;
        lines.push(normX(x.x),normY(x.y),1,opacity,normX(y.x),normY(y.y),1,opacity);
      }
      drawItems(lines,gl.LINES,false);
      drawItems(dots,gl.POINTS,true);
    }
    return {draw:draw, resize:function(width,height){gl.viewport(0,0,width,height);},
      healthy:function(){return !gl.isContextLost();}};
  }
  window.__dulukaBgWebgl={create:create};
})(window);
