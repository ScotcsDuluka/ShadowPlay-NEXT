'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.join(__dirname, '..', 'osc');
const source = fs.readFileSync(path.join(root, 'mods', 'notifier', 'OSC-Adapter.js'), 'utf8');
const router = fs.readFileSync(path.join(root, 'mods', 'notifier', 'notifier-system', 'notifier.js'), 'utf8');
const css = fs.readFileSync(path.join(root, 'mods', 'notifier', 'notifier-system', 'notifier.css'), 'utf8');

function setup() {
    const callbacks = {};
    const sockets = [];
    const actions = [];
    const active = new Set();
    const storage = new Map();
    let slotCount = 0, startup = null;
    const log = {warn() {}, error() {}, info() {}};
    const fakeFetch = async (url) => {
        if (url === '/Notifier/v.1.0/settings')
            return {ok:true, json:async()=>({slotCount:3})};
        if (url === '/Duluka/v1/gpu/manager')
            return {ok:true,json:async()=>({profiles:[
                {exePath:'C:\\Games\\Smash Legends\\SMASH.exe',iconDataUri:'data:image/png;base64,iVBORw0'},
                {exePath:'C:\\Games\\Other\\OTHER.exe',iconDataUri:'data:image/png;base64,iVBORw0'}
            ]})};
        return {ok:true,json:async()=>({games:[]})};
    };
    const fakeDocument={
        createElement(tag){
            if (tag!=='canvas')throw Error('unexpected '+tag);
            return {getContext:()=>({font:'',measureText:(s)=>({width:String(s).length*7})})};
        }
    };
    class FakeImage {
        constructor(){this.naturalWidth=32;this.naturalHeight=32;}
        set src(v){this._src=v;Promise.resolve().then(()=>{if(this.onload)this.onload();});}
        get src(){return this._src;}
    }
    const win={
        innerWidth:1800,
        localStorage:{
            getItem(key){return storage.has(key)?storage.get(key):null;},
            setItem(key,value){storage.set(key,String(value));}
        },
        setTimeout(){return 1;}, clearTimeout(){},
        Notifier:{
            show(message,options) {
                actions.push({message,options});
                active.add(options.group);
                return undefined; // real Notifier.show routes internally, returns void.
            },
            hasActiveGroupPrefix(prefix) {
                return Array.from(active).some(x=>x.indexOf(prefix)===0);
            },
            getOpenShareShortcut:async()=> 'Alt+Z',
            setSlotCount(n){slotCount=n;},
            startHeartbeat(){},
            status(){return '';}
        },
        angular:{module(name){assert.equal(name,'main');return {run(list){startup=list[list.length-1];}}}}
    };
    vm.runInNewContext(source,{
        window:win,angular:win.angular,document:fakeDocument,Image:FakeImage,
        fetch:fakeFetch,Promise,Date,JSON,Math,isFinite,console
    },{filename:'OSC-Adapter.js'});
    assert.equal(typeof startup,'function');
    const eventAggregator={
        on(name,cb){callbacks[name]=cb;},
        trigger(name,payload){if(callbacks[name])callbacks[name](payload);}
    };
    startup(
        (name)=>{assert.equal(name,'translate');return(key,params)=>{
            if (key==='l10n.notificationOpenShare') return 'Press '+(params.arg1||'Alt+Z')+' to open Overlay';
            return key;
        };},
        log,eventAggregator,'notificationEvent',[],
        {register(name,alias){sockets.push({name,alias});}}
    );
    async function drain(){for(let i=0;i<30;i++)await Promise.resolve();}
    function status(games){
        const now=new Date().toISOString();
        return {schemaVersion:1,eventId:'evt-'+Math.random(),publishedUtc:now,status:{
            enabled:true,running:true,catalogReady:true,catalogEmpty:false,
            catalogVersion:'1.2.7',matchedGames:games,matchedGameCount:games.length
        }};
    }
    function game(pid,gameId='smash.legends',matched='2026-10-22T07:15:00.000Z') {
        const isSmash=gameId==='smash.legends';
        return {gameId,processId:pid,matchedUtc:matched,catalogVersion:'1.2.7',
            displayName:isSmash?'SMASH LEGENDS':'OTHER GAME',
            executablePath:isSmash?'C:\\Games\\Smash Legends\\SMASH.exe':'C:\\Games\\Other\\OTHER.exe'};
    }
    return {actions,active,storage,sockets,callbacks,status,game,drain,
        dispatch(games){callbacks['nv-game-watcher'](status(games));},
        get slotCount(){return slotCount;}
    };
}

test('GameWatcher identical snapshots do not create or flip a new toast',async()=>{
    const h=setup();
    const g=h.game(8101);
    h.dispatch([g]);await h.drain();
    assert.equal(h.actions.length,1);
    assert.equal(h.actions[0].options.priority,true);
    assert.equal(h.actions[0].options.updaterUi,false);
    h.dispatch([g]);await h.drain();
    assert.equal(h.actions.length,1,'repeated GameWatcher snapshot must not spam updater dance');
    assert.equal(h.active.size,1);
    assert.equal(h.slotCount,3);
});

test('new PID or matchedUtc for the same game updates the same toast group',async()=>{
    const h=setup();
    const first=h.game(8110);
    h.dispatch([first]);await h.drain();
    const prior=h.actions[0];
    const next=h.game(8111, 'smash.legends','2026-10-22T07:16:00.000Z');
    next.executablePath='D:\\AnotherFolder\\SMASH.exe'; // Same catalog game, alternate install path.
    h.dispatch([next]);await h.drain();
    assert.equal(h.actions.length,2);
    assert.equal(h.actions[1].options.updaterUi,true);
    assert.equal(h.actions[1].options.group,prior.options.group);
    assert.equal(h.active.size,1);
    assert.match(h.actions[1].options.group,/smash\.legends/);
    h.dispatch([next]);await h.drain();
    assert.equal(h.actions.length,2);
});

test('concurrent GameWatcher instances with the same game coalesce while icon lookup is pending',async()=>{
    const h=setup();
    h.dispatch([h.game(8001),h.game(8002)]);
    await h.drain();
    assert.equal(h.actions.length,1);
    h.dispatch([h.game(8001),h.game(8002)]);await h.drain();
    assert.equal(h.actions.length,2,'second run can update the existing card');
    assert.equal(h.actions[1].options.updaterUi,true);
    assert.equal(h.actions[1].options.group,h.actions[0].options.group);
});

test('different games remain distinct, and a later run after toast closed gets a fresh card',async()=>{
    const h=setup();
    h.dispatch([h.game(7001)]);await h.drain();
    h.dispatch([h.game(7002,'another.game')]);await h.drain();
    assert.equal(h.actions.length,2);
    assert.notEqual(h.actions[0].options.group,h.actions[1].options.group);
    assert.equal(h.actions[1].options.updaterUi,false);
    h.active.delete(h.actions[0].options.group);
    h.dispatch([h.game(7003)]);await h.drain();
    assert.equal(h.actions.length,3);
    assert.equal(h.actions[2].options.updaterUi,false);
});

test('router retains a single priority group including mid-transition and uses updater dance',()=>{
    assert.match(router,/Same semantic priority group always owns one card/);
    assert.match(router,/entry\.updaterUi && \(!updateUnit\.inTransition \|\| updateUnit\.dancing\)/);
    const idx=router.indexOf('if (entry.priority)');
    const duplicateGuard=router.indexOf('sameToastGroup(updateUnit.group, group)',idx);
    const freeAllocation=router.indexOf('for (var freeSlot',idx);
    assert.ok(duplicateGuard>idx && duplicateGuard<freeAllocation,'check existing group before allocating');
    assert.match(css,/\.nv-toast__green[\s\S]*?clip-path:\s*inset\(0 0 1px 0\)/);
    assert.match(css,/\.nv-toast__black[\s\S]*?border-left:\s*var\(--nv-strip\) solid var\(--nv-green\)/);
});

test('live priority router updates existing toast rather than allocate a new slot',()=>{
    const start=router.indexOf('function updateNotifier(message, entry, group) {');
    const end=router.indexOf('/* DisplaceFirstNormalSlot',start);
    assert.ok(start>0&&end>start,'must locate router body');
    const unit={group:'gpu-game-openshare:smash.legends',inTransition:false,dancing:false,steady:true};
    const updates=[];
    let allocations=0;
    const sandbox={
        updateSlotLiveness(){},configuredSlotCount:()=>3,
        notifierRouter:{slots:{1:unit,2:undefined,3:undefined}},
        sameToastGroup:(a,b)=>!!a&&String(a).toLowerCase()===String(b).toLowerCase(),
        danceToast(card,message,entry,group){updates.push({card,message,entry,group});},
        registerActiveSlot(){allocations++;},
        createUnit(){allocations++;return{};},
        choreographEntrance(){allocations++;},
        compactStack(){},assignUnitY(){return 0;},isProtectedToastGroup(){return false;},
        isToggleGroup(){return false;},displaceFirstNormalSlot(){allocations++;},
        console:{log(){}}
    };
    vm.runInNewContext(router.slice(start,end),sandbox,{filename:'updateNotifier.js'});
    const message='SMASH LEGENDS';
    const entry={priority:true,updaterUi:true};
    sandbox.updateNotifier(message,entry,unit.group);
    assert.equal(updates.length,1);
    assert.equal(updates[0].card,unit);
    assert.equal(allocations,0);
    unit.inTransition=true;
    sandbox.updateNotifier(message,entry,unit.group);
    assert.equal(updates.length,1,'exiting card should not reanimate or allocate a sibling');
    assert.equal(allocations,0);
    unit.dancing=true;
    sandbox.updateNotifier(message,entry,unit.group);
    assert.equal(updates.length,2,'dancing card coalesces new updater messages');
    assert.equal(allocations,0);
    sandbox.updateNotifier(message,{priority:true,updaterUi:false},unit.group);
    assert.equal(allocations,0,'duplicate group cannot take another slot even without updaterUi');
});
