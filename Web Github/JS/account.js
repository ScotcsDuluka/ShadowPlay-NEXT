/* ShadowPlay NEXT account landing — decorative particle background only.
   No account inputs, API access, tracking or credential storage. */
(function () {
  'use strict';
  const canvas = document.getElementById('network');
  if (!canvas || !canvas.getContext) return;
  const ctx = canvas.getContext('2d');
  if (!ctx) return;
  const reduced = window.matchMedia &&
    window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const points = [];
  let width=0, height=0, dpi=1, raf=0, last=0;
  const seeded=(n) => {const v=Math.sin(n*91.1+19.71)*17548.13;return v-Math.floor(v);};

  function resize() {
    width = Math.max(320,window.innerWidth || document.documentElement.clientWidth);
    height = Math.max(400,window.innerHeight || document.documentElement.clientHeight);
    dpi = Math.min(2,window.devicePixelRatio || 1);
    canvas.width = Math.round(width*dpi);
    canvas.height = Math.round(height*dpi);
    ctx.setTransform(dpi,0,0,dpi,0,0);
    points.length=0;
    const count = Math.min(58,Math.max(20,Math.round(width*height/31000)));
    for (let i=0;i<count;i++) {
      points.push({
        x:seeded(i*5+11)*width, y:seeded(i*5+17)*height,
        vx:(seeded(i*5+23)-.5)*.44,
        vy:(seeded(i*5+29)-.5)*.44,
        alpha:.31+seeded(i*5+31)*.52
      });
    }
    if(reduced) paint(false);
  }
  function paint(animate) {
    ctx.clearRect(0,0,width,height);
    for(let i=0;i<points.length;i++){
      const p=points[i];
      if(animate){
        p.x+=p.vx;p.y+=p.vy;
        if(p.x<0 || p.x>width)p.vx=-p.vx;
        if(p.y<0 || p.y>height)p.vy=-p.vy;
      }
      ctx.fillStyle='rgba(130,207,0,'+p.alpha.toFixed(2)+')';
      ctx.fillRect(p.x,p.y,2,2);
      for(let j=i+1;j<points.length;j++){
        const q=points[j],dx=p.x-q.x,dy=p.y-q.y;
        const dist=dx*dx+dy*dy;
        if(dist>23000)continue;
        ctx.strokeStyle='rgba(118,185,0,'+(.34*(1-dist/23000)).toFixed(3)+')';
        ctx.lineWidth=.75;
        ctx.beginPath();ctx.moveTo(p.x,p.y);ctx.lineTo(q.x,q.y);ctx.stroke();
      }
    }
  }
  function frame(t) {
    raf=0;
    if(document.hidden || reduced)return;
    if(t-last>38){last=t;paint(true);}
    raf=window.requestAnimationFrame(frame);
  }
  function visibility(){
    if(document.hidden && raf){window.cancelAnimationFrame(raf);raf=0;}
    else if(!document.hidden && !reduced && !raf)raf=window.requestAnimationFrame(frame);
  }
  window.addEventListener('resize',resize,{passive:true});
  document.addEventListener('visibilitychange',visibility);
  resize();
  if(!reduced)raf=window.requestAnimationFrame(frame);
})();