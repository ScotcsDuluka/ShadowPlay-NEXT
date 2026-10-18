// cefquery-shim.js — window.cefQuery พร้อมก่อน angular boot (แบบของแท้:
// native ปล่อย bridge ก่อนสคริปต์หน้า) — ส่ง query ผ่าน HTTP /cefquery
(function () {
  if (window.cefQuery) return;
  function post(body, pers) {
    return fetch('/cefquery', { method: 'POST', headers: { 'X-OSC-PERSISTENT': pers ? '1' : '0' }, body: body });
  }
  window.cefQuery = function (q) {
    if (!q || typeof q.request !== 'string') { q && q.onFailure && q.onFailure(1, 'bad query'); return 0; }
    var pers = !!q.persistent;
    post(q.request, pers).then(function (r) {
      if (!pers) return r.text().then(function (t) { q.onSuccess && q.onSuccess(t); });
      (function wait() {
        fetch('/close-event').then(function (r) { return r.status === 200 ? r.text() : null; })
          .then(function (t) { if (t) { q.onSuccess && q.onSuccess(t); } else { setTimeout(wait, 500); } })
          .catch(function () { setTimeout(wait, 1000); });
      })();
      return null;
    }).catch(function (e) { q.onFailure && q.onFailure(1, String(e)); });
    return 0;
  };
  window.cefQueryCancel = function () {};
})();
