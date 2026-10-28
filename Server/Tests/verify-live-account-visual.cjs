'use strict';
// Read-only visual acceptance for the real HTTPS Portal.
// Never posts credentials, changes sessions or accesses local admin.
const assert = require('node:assert/strict');
const path = require('node:path');
const fs = require('node:fs');
const puppeteer = require('C:/Users/ScotcsDuluka/AppData/Local/npm-cache/_npx/4b4c857f6efdfb61/node_modules/puppeteer');
const base='https://aorus-desktop.taile6314b.ts.net';
const save=path.resolve(__dirname,'../Duluka.Server/bin/DeploymentBackups/portal-ui-20261024-220402-482');
(async function(){
  const browser=await puppeteer.launch({
    headless:true,
    executablePath:'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    args:['--disable-gpu','--no-first-run','--no-default-browser-check']
  });
  try {
    const c=await fetch(base+'/portal/config').then(r=>r.json());
    assert.equal(c.accountMode,'main');
    assert.equal(c.registrationEnabled,false);
    for (const [name,width,height] of [['desktop',1440,900],['mobile',390,844]]) {
      const page=await browser.newPage();
      await page.setViewport({width,height,deviceScaleFactor:1,isMobile:name==='mobile',hasTouch:name==='mobile'});
      const errors=[];
      page.on('pageerror',e=>errors.push(e.message));
      const res=await page.goto(base+'/portal',{waitUntil:'networkidle2',timeout:16000});
      assert.equal(res.status(),200);
      const state=await page.evaluate(()=>{
        const text=(selector)=>document.querySelector(selector)?.textContent?.trim();
        const visible=(selector)=>{
          const el=document.querySelector(selector);
          return !!el && !el.hidden && getComputedStyle(el).display !== 'none';
        };
        return {
          h1:text('h1'),hasFrame:!!document.querySelector('.gateway-frame'),
          loginVisible:visible('#login-form'),registerVisible:visible('#register-form'),
          registrationTabVisible:visible('#show-register'),
          scrollWidth:document.documentElement.scrollWidth,
          clientWidth:document.documentElement.clientWidth,
          width:window.innerWidth,
          mode:text('#environment-label'),
        };
      });
      assert.match(state.h1,/DULUKA/);
      assert.match(state.h1,/ACCOUNT/);
      assert.equal(state.hasFrame,true);
      assert.equal(state.loginVisible,true);
      assert.equal(state.registrationTabVisible,false);
      assert.ok(state.scrollWidth<=state.clientWidth+1,name+' horizontal overflow');
      assert.equal(errors.length,0,'page errors: '+errors.join(';'));
      const out=path.join(save,'live-'+name+'.png');
      fs.mkdirSync(save,{recursive:true});
      await page.screenshot({path:out,fullPage:true});
      console.log('PASS '+name+' '+JSON.stringify({state,image:out}));
      await page.close();
    }
    console.log('LIVE_ACCOUNT_VISUAL_PASS');
  }finally{await browser.close()}
})().catch(err=>{console.error('FAIL '+err.stack);process.exitCode=1;});
