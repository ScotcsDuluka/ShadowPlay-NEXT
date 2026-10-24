/* Decorative connected-particle network: no input listeners, no cookies,
 * no telemetry and zero access to account/password fields. */
(() => {
  'use strict';
  const canvas = document.getElementById('constellation-bg');
  if (!canvas || !canvas.getContext) return;
  const ctx = canvas.getContext('2d', { alpha: true });
  if (!ctx) return;
  const reduce = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const points = [];
  let width = 0, height = 0, ratio = 1, last = 0;
  let handle = null;
  let visible = !document.hidden;
  const random = (seed) => {
    const t = Math.sin(seed * 91.7 + 17.7) * 43758.5453;
    return t - Math.floor(t);
  };
  function resize() {
    width = Math.max(320, window.innerWidth || document.documentElement.clientWidth);
    height = Math.max(400, window.innerHeight || document.documentElement.clientHeight);
    ratio = Math.min(window.devicePixelRatio || 1, 2);
    canvas.width = Math.round(width * ratio);
    canvas.height = Math.round(height * ratio);
    canvas.style.width = width + 'px';
    canvas.style.height = height + 'px';
    ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
    const number = Math.max(14, Math.min(58, Math.round((width * height) / 26000)));
    points.length = 0;
    for (let i = 0; i < number; i++) {
      points.push({
        x: random(i * 2 + 5) * width,
        y: random(i * 2 + 6) * height,
        dx: (random(i * 4 + 27) - .5) * .26,
        dy: (random(i * 4 + 42) - .5) * .26,
      });
    }
    if (reduce) draw(false);
  }
  function draw(move) {
    ctx.clearRect(0, 0, width, height);
    for (let i = 0; i < points.length; i++) {
      const a = points[i];
      if (move) {
        a.x += a.dx;
        a.y += a.dy;
        if (a.x < 0 || a.x > width) a.dx = -a.dx;
        if (a.y < 0 || a.y > height) a.dy = -a.dy;
      }
      ctx.fillStyle = 'rgba(130,210,3,0.68)';
      ctx.fillRect(a.x, a.y, 2, 2);
      for (let j = i + 1; j < points.length; j++) {
        const b = points[j];
        const dx = a.x - b.x, dy = a.y - b.y;
        const distance = dx * dx + dy * dy;
        if (distance > 21000) continue;
        const alpha = (1 - distance / 21000) * .35;
        ctx.strokeStyle = 'rgba(115,190,0,' + alpha.toFixed(3) + ')';
        ctx.lineWidth = .85;
        ctx.beginPath(); ctx.moveTo(a.x, a.y); ctx.lineTo(b.x, b.y); ctx.stroke();
      }
    }
  }
  function loop(time) {
    if (!visible) { handle = null; return; }
    if (time - last > 45) { last = time; draw(true); }
    handle = window.requestAnimationFrame(loop);
  }
  function onVisibility() {
    visible = !document.hidden;
    if (visible && !reduce && handle === null) handle = window.requestAnimationFrame(loop);
    if (!visible && handle !== null) { window.cancelAnimationFrame(handle); handle = null; }
  }
  window.addEventListener('resize', resize, { passive: true });
  document.addEventListener('visibilitychange', onVisibility);
  resize();
  if (!reduce) handle = window.requestAnimationFrame(loop);
})();