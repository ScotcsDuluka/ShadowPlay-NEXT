'use strict';
const test=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const root=path.join(__dirname,'..');
const html=fs.readFileSync(path.join(root,'ui/index.html'),'utf8');
const app=fs.readFileSync(path.join(root,'ui/app.js'),'utf8');
const css=fs.readFileSync(path.join(root,'ui/quick-ui.css'),'utf8');
const handler=fs.readFileSync(path.join(root,'src/launcher_query_handler.cpp'),'utf8');
const i18n=fs.readFileSync(path.join(root,'ui/i18n.js'),'utf8');

test('account entry is present on the three-tab Launcher without new login fields',()=>{
  assert.match(html,/id="btnAccount"/);
  assert.match(html,/data-i18n="accountPortal"/);
  assert.match(css,/\.quick-account-button/);
  assert.doesNotMatch(html,/id="account-password"/);
});
test('account button opens only the fixed HTTPS external browser portal',()=>{
  assert.match(app,/rpc\('LAUNCHER_OPEN_ACCOUNT_PORTAL'\)/);
  assert.match(handler,/if \(cmd == "LAUNCHER_OPEN_ACCOUNT_PORTAL"\)/);
  assert.match(handler,/L"https:\/\/aorus-desktop\.taile6314b\.ts\.net\/portal"/);
  assert.match(handler,/ShellExecuteW\(NULL, L"open"/);
  assert.match(handler,/frame_url != launch->trusted_url/);
  assert.doesNotMatch(app,/localStorage\.(setItem|getItem)\([^\n]*password/);
});
test('English and Thai labels describe the external account entry',()=>{
  assert.match(i18n,/accountPortal: 'ACCOUNT/);
  assert.match(i18n,/accountPortal: 'บัญชีผู้ใช้/);
  assert.match(i18n,/accountPortalHint:/);
});
