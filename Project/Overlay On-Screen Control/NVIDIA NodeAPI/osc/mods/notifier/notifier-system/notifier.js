/* ============================================================================
   45-notifier.js — TOAST NOTIFIER (ฉบับเต็ม faithful)
   ----------------------------------------------------------------------------
   PORT ครบจาก Notifier WinForms ทุกไฟล์:
     [API]\TCP\[Notifier] Client.vb  → SECTION 2/3/4 (registry/gates/groups)
     [API]\Loader.vb                 → SECTION 5 (slot router T29/T30) + SECTION 6 (stack T30)
     [UI OVERLAY]\[Notifier]\[2] Background.vb    → SECTION 7 (choreography + dance + exit)
     [UI OVERLAY]\[Notifier]\[1] String+Icon+Logo.vb → SECTION 7 (rider reveal)
     [UI OVERLAY]\[Notifier]\[3] Shadow.vb        → CSS box-shadow (แทน)
   เอกสารกำกับ: docs\port-map\00-method.md + 01-notifier.md
   กฎ (Owner): vanilla · verbose · ทุกค่าอ้างบรรทัด .vb · ห้ามเดา
   ========================================================================== */

/* ============================================================================
   SECTION 1 — ค่าคงที่ (อ้างบรรทัด .vb ทุกตัว)
   ========================================================================== */

var NOTIFIER_CONST = {
    /* Timing การเข้า/ออก/flip ทั้งหมดย้ายไป CSS แล้ว (ค่าแท้ทุกตัว — ดู css/45-notifier.css :root)
       เหลือเฉพาะตัวที่ JS ใช้จริง:                                                   */
    AUTOCLOSE_MS: 6000,         /* autoClose 6000 (WinForms) — osc แท้ไม่มี timer (หน้าคุมเอง) คงไว้ตาม WinForms */
    EXIT_MS: 600,               /* รอ slide-out จบก่อนถอน DOM */
    DANCE_OUT_MS: 300,          /* Faster updater dance, preserving the full exit */
    DANCE_TURNAROUND_MS: 300,   /* Swap only after the card is fully offstage */
    DANCE_IN_MS: 300,           /* StartSlide black panel กลับเข้า */
    RIDER_FADE_MS: 200,         /* WinForms rider fade: 20 ticks × 10ms */
    STACK_PITCH_PX: 100,        /* StackPitchPx = 100 (card 90 + gap 10) */
    STACK_HEARTBEAT_MS: 100,    /* heartbeat ทุก 100ms (liveness + compact) */
    MAX_SLOTS: 3,               /* Notifier + Notifier2 + Notifier3 */
    DEFAULT_SLOT_COUNT: 2       /* ConfiguredSlotCount default = 2 — WinForms */
};

/* ============================================================================
   SECTION 2 — NOTIFICATION REGISTRY (Client.vb InitNotifications บรรทัด 32-98)
   ----------------------------------------------------------------------------
   icon = PUA codepoint จริงจาก .vb (สแกน raw → ยืนยันกับ cmap nvgcshare.woff2)
   positive = แท้ส่ง greenColor มา (ข่าวดี)
   ========================================================================== */

var NOTIFICATION_REGISTRY = {
    'l10n.test':                                      { locKey: '' },
    'l10n.notificationWarningDesktopCaptureDisabled': { locKey: '', icon: '\uE932' },
    'l10n.notificationScreenshotSavedToGallery':      { locKey: '', icon: '\uE912' },
    'l10n.ramwram':                                   { locKey: '', icon: '\uE940' },
    'l10n.ramwram95':                                 { locKey: '', icon: '\uE940' },
    'l10n.ramwramcritical':                           { locKey: '', icon: '\uE940' },
    'l10n.cpuwram':                                   { locKey: '', icon: '\uE940' },
    'l10n.diskspacelow':                              { locKey: '', icon: '\uE940' },
    'l10n.irOn':                                      { locKey: '', icon: '\uE955' },
    'l10n.notificationWarningGameRequired':           { locKey: '', icon: '\uE940' },
    'l10n.notificationWarningNvidiaGpuRequired':      { locKey: '', icon: '\uE940' },
    'l10n.validsavepath':                             { locKey: '', icon: '\uE940' },
    'l10n.notificationManualRecordStarted':           { locKey: '', icon: '\uE933', positive: true },
    'l10n.notificationManualRecordStopped':           { locKey: '', icon: '\uE933' },
    'l10n.notificationReplaySaveError':               { locKey: '', icon: '\uE935' },
    'l10n.notificationTurnOnInstantReplay':           { locKey: '', icon: '\uE940' },
    'l10n.notificationInstantReplayStopped':          { locKey: '', icon: '\uE935' },
    'l10n.notificationInstantReplayStarted':          { locKey: '', icon: '\uE935', positive: true },
    'l10n.notificationWarningPhotographyNotAllowed':  { locKey: '', icon: '\uE940' },
    'l10n.notificationFeatureNotReady':               { locKey: '', icon: '\uE940' },
    'l10n.notificationCustomOverlayFileNotFound':     { locKey: '', icon: '\uE931' },
    'l10n.notificationaccountconfirmerror':           { locKey: '', icon: '\uE902' },
    'l10n.notifierOpen':                              { locKey: '' },
    'l10n.notifierNotUsing':                          { locKey: '' },
    'l10n.notificationAppClosed':                     { locKey: '' },
    'l10n.notificationSharedClose':                   { locKey: '' },
    'l10n.notificationUpdateAvailable':               { locKey: '', icon: '\uE949' },
    'l10n.notificationVersionLatest':                 { locKey: '', icon: '\uE94A' },
    'l10n.notificationErrorResolution':               { locKey: '', icon: '\uE940' },
    'l10n.notificationErrorGeneral':                  { locKey: '', icon: '\uE94A' },
    'l10n.foldererror':                               { locKey: '', icon: '\uE94A' },
    'l10n.Capture_notuse':                            { locKey: '', icon: '\uE934' },
    'l10n.openLocation':                              { locKey: '', icon: '\uE906' },
    'l10n.privacy':                                   { locKey: '', icon: '\uEC04' },
    /* keys จริงจาก log (บรรทัด 72-92) */
    'l10n.feature_not_ready':       { locKey: 'l10n.notificationFeatureNotReady', icon: '\uE940' },
    'l10n.replay_turn_on':          { locKey: 'l10n.notificationTurnOnInstantReplay', icon: '\uE940' },
    'l10n.instant_replay_on':       { locKey: 'l10n.notificationInstantReplayStarted', icon: '\uE935', positive: true },
    'l10n.instant_replay_off':      { locKey: 'l10n.notificationInstantReplayStopped', icon: '\uE935' },
    'l10n.saved_last_15':           { locKey: 'l10n.notificationInstantReplaySaved', icon: '\uE935' },
    'l10n.account_confirm_error':   { locKey: 'l10n.notificationaccountconfirmerror', icon: '\uE902' },
    'l10n.replay_error':            { locKey: 'l10n.notificationReplaySaveError', icon: '\uE935' },
    'l10n.recording_started':       { locKey: 'l10n.notificationManualRecordStarted', icon: '\uE933', positive: true },
    'l10n.recording_saved':         { locKey: 'l10n.notificationManualRecordStopped', icon: '\uE933' },
    'l10n.recording_error':         { locKey: 'l10n.notificationErrorGeneral' },
    'l10n.update_available':        { locKey: 'l10n.notificationUpdateAvailable', icon: '\uE949' },
    'l10n.version_latest':          { locKey: 'l10n.notificationVersionLatest', icon: '\uE933' },
    'l10n.notificationErrorEngineNotRunning': { locKey: 'l10n.notificationErrorEngineNotRunning', icon: '\uE90E' },
    /* กลุ่มมี args (บรรทัด 95-97) */
    'l10n.testarg':                            { locKey: '', args: ['1', '2'] },
    'l10n.notificationInstantReplaySavedFull': { locKey: 'l10n.notificationInstantReplaySaved', icon: '\uE935' },
    'l10n.notificationOpenShare':              { locKey: '' }
};

/* ============================================================================
   SECTION 3 — CATEGORIES (27 gates) — port ตรงจาก NotificationCategory()
   (Client.vb บรรทัด 214-282 — ทุก case 1:1)
   ========================================================================== */

var NOTIFICATION_CATEGORY_MAP = {
    recording_started: 'RecordingStarted',
    recording_saved: 'RecordingSaved',
    recording_error: 'RecordingError',
    instant_replay_on: 'InstantReplayOn',
    instant_replay_off: 'InstantReplayOff',
    replay_turn_on: 'ReplayTurnOn',
    replay_error: 'ReplayError',
    notificationinstantreplaysaved: 'ReplaySaved',
    saved_last_15: 'ReplaySaved',
    notificationscreenshotsavedtogallery: 'ScreenshotSaved',
    validsavepath: 'ValidSavePath',
    notificationopenshare: 'OpenShare',
    ramwram: 'RamWarning',
    ramwram95: 'RamWarning95',
    ramwramcritical: 'RamCritical',
    cpuwram: 'CpuWarning',
    diskspacelow: 'DiskSpaceLow',
    update_available: 'UpdateAvailable',
    version_latest: 'VersionLatest',
    notificationerrorgeneral: 'UpdateError',
    account_confirm_error: 'AccountConfirmError',
    extension_not_found: 'ExtensionNotFound',
    feature_not_ready: 'FeatureNotReady',
    notificationwarningnvidiagpurequired: 'GpuRequired',
    notificationerrorenginenotrunning: 'EngineNotRunning',
    notificationerrorengineuiinuse: 'EngineUIInUse',
    notificationerrorresolution: 'ErrorResolution',
    notificationwarningdesktopcapturedisabled: 'DesktopCaptureDisabled'
};

/* NotificationGroup() (Client.vb บรรทัด 297-309) */
var NOTIFICATION_GROUP_MAP = {
    RecordingStarted: 'recording',
    RecordingSaved: 'recording',
    RecordingError: 'recording',
    InstantReplayOn: 'replay',
    InstantReplayOff: 'replay',
    ReplayTurnOn: 'replay',
    ReplayError: 'replay',
    ReplaySaved: 'replay'
};

function notificationCategory(key) {
    var k = String(key || '');
    if (k.toLowerCase().indexOf('l10n.') === 0) k = k.substring(5);
    k = k.toLowerCase();
    return NOTIFICATION_CATEGORY_MAP[k] || null;   /* null = fail-open */
}

function notificationGroup(key) {
    var category = notificationCategory(key);
    if (category === null) return key;
    return NOTIFICATION_GROUP_MAP[category] || category;
}

function getOpenShareShortcut() {
    var injector = window.angular && (
        window.angular.element(document.documentElement).injector() ||
        window.angular.element(document.body).injector());
    if (!injector) return Promise.reject(new Error('OSC Angular injector is unavailable.'));

    var shadowPlayService = injector.get('shadowPlayService');
    var keyboardService = injector.get('keyboardService');
    return shadowPlayService.getHotkeyShortcut(
        shadowPlayService.HotkeyShortcuts.OPENSHARE).then(function (shortcut) {
        var keys = shortcut && shortcut.keys;
        if (!Array.isArray(keys) || keys.length === 0)
            throw new Error('OpenShare shortcut is not configured.');

        var label = keyboardService.shortcutToStr(keys);
        if (!label || label === 'None')
            throw new Error('OpenShare shortcut could not be formatted.');
        return label;
    });
}

function getNotificationText(locKey, args) {
    var rootElement = window.angular &&
        window.angular.element(document.documentElement);
    var injector = rootElement && rootElement.injector();
    if (!injector)
        throw new Error('OSC Angular injector is unavailable for notification translation.');

    var interpolation = {};
    (args || []).forEach(function (value, index) {
        interpolation['arg' + (index + 1)] = value;
    });
    return injector.get('$filter')('translate')(locKey, interpolation);
}

/* ============================================================================
   SECTION 4 — GATES + SLOT CONFIG
   ----------------------------------------------------------------------------
   NotificationAllowed (Client.vb บรรทัด 207-211): fail-open เสมอ
   ConfiguredSlotCount (Loader.vb บรรทัด 315-320): 1-3, default 2, อ่านใหม่ทุก toast
   แหล่งค่าในเว็บ: NOTIFIER_CONFIG ใส่จาก host/plane (browser เขียน registry ไม่ได้)
   ========================================================================== */

var NOTIFIER_CONFIG = {
    gates: {},                      /* { RecordingStarted: true, ... } — default true */
    slotCount: NOTIFIER_CONST.DEFAULT_SLOT_COUNT
};

function notificationAllowed(key) {
    var category = notificationCategory(key);
    if (category === null) return true;                 /* fail-open */
    var v = NOTIFIER_CONFIG.gates[category];
    return (typeof v === 'undefined') ? true : v === true;   /* fail-open */
}

function configuredSlotCount() {
    var n = Number(NOTIFIER_CONFIG.slotCount);
    if (n < 1) n = 1;
    if (n > 3) n = 3;
    return n;
}

function sameToastGroup(a, b) {
    return !!a && String(a).toLowerCase() === String(b).toLowerCase();
}

/* IsToggleGroup (Loader.vb บรรทัด 849-851): recording/replay = start/stop pair
   — slot ที่ถือ group นี้ "กำลังติดตามสถานะ" ห้ามถูก displace ก่อน slot normal */
function isToggleGroup(group) {
    return sameToastGroup(group, 'recording') || sameToastGroup(group, 'replay');
}

function isProtectedToastGroup(group) {
    return /^gpu-/.test(String(group || '').toLowerCase());
}

/* ============================================================================
   SECTION 5 — SLOT ROUTER (Loader.vb UpdateNotifier บรรทัด 497-604 ครบทุกกฎ)
   ----------------------------------------------------------------------------
   โมเดล: slots = [ {slot:1, unit}, {slot:2, unit}, {slot:3, unit} ]
   unit = หนึ่ง toast ที่มี state: { group, steady, inTransition, dancing, el }
   กฎ (เรียงตามลำดับที่แท้ตรวจ):
     1) group live บน side slot + steady (green_stop) → DanceSideToast ที่ slot นั้น
     2) main busy + group ใหม่ → side slot ว่างแรก (fresh show ที่ BOTTOM ของ stack)
        ทุก slot busy → DisplaceFirstNormalSlot (no-queue, OWNER rule)
     2b) group live บน side slot แต่ไม่ steady → mid-exit: displace / อื่น: dance (coalesce)
     3) main → mid-exit: displace / อื่น: ShowOnMain (fresh = BOTTOM ของ stack, dance, coalesce)
   ========================================================================== */

var notifierRouter = {
    slots: {},           /* slotIdx(1-3) → unit | undefined */
    slotOrder: [],       /* active slot indices — first = top ของ stack (แท้ _slotOrder) */
    wasAlive: { 1: false, 2: false, 3: false },
    stackEl: null
};

var lastNativeNotifierRects = null;
var nativeNotifierRectsPending = false;
var queuedNativeNotifierRects = null;

function sendNativeNotifierRects(signature, rects) {
    nativeNotifierRectsPending = true;
    window.cefQuery({
        request: JSON.stringify({
            command: 'QUERY_OSC_SET_NOTIFIER_RECTS',
            displayRects: rects
        }),
        onSuccess: function () {
            lastNativeNotifierRects = signature;
            nativeNotifierRectsPending = false;
            var queued = queuedNativeNotifierRects;
            queuedNativeNotifierRects = null;
            if (queued && queued.signature !== lastNativeNotifierRects)
                sendNativeNotifierRects(queued.signature, queued.rects);
        },
        onFailure: function () {
            lastNativeNotifierRects = null;
            nativeNotifierRectsPending = false;
            queuedNativeNotifierRects = null;
        }
    });
}

function syncNativeNotifierRects() {
    if (typeof window.cefQuery !== 'function') return;
    var rects = [];
    if (notifierRouter.stackEl) {
        var cards = notifierRouter.stackEl.querySelectorAll('.nv-toast.is-open:not(.is-leaving)');
        for (var i = 0; i < cards.length; i++) {
            var rect = cards[i].getBoundingClientRect();
            if (rect.width > 0 && rect.height > 0) {
                rects.push({
                    x: rect.left,
                    y: rect.top,
                    width: rect.width,
                    height: rect.height
                });
            }
        }
    }
    var signature = JSON.stringify(rects);
    if (nativeNotifierRectsPending) {
        queuedNativeNotifierRects = { signature: signature, rects: rects };
    } else if (signature !== lastNativeNotifierRects) {
        sendNativeNotifierRects(signature, rects);
    }
}

window.addEventListener('hashchange', function () {
    lastNativeNotifierRects = null;
    syncNativeNotifierRects();
});

function notifierEnsureStack() {
    if (notifierRouter.stackEl && document.body.contains(notifierRouter.stackEl)) {
        return notifierRouter.stackEl;
    }
    var stack = document.createElement('div');
    stack.className = 'nv-notifier-stack';
    stack.id = 'nv-notifier-stack';
    document.body.appendChild(stack);
    notifierRouter.stackEl = stack;
    return stack;
}

/* SlotIsAlive (แท้ บรรทัด 768-775): unit มีชีวิต = Visible หรือ green_stop visible
   เว็บ: toast มีชีวิตตั้งแต่ spawn จนถูกถอน (liveToasts list) */
function slotIsAlive(slotIdx) {
    return !!notifierRouter.slots[slotIdx];
}

function clearSlotGroup(slotIdx) {
    var unit = notifierRouter.slots[slotIdx];
    if (unit) unit.group = '';
}

/* UpdateSlotLiveness (แท้ บรรทัด 785-795): slot ตาย → ออกจาก stack order + เคลียร์ group */
function updateSlotLiveness() {
    for (var i = 1; i <= 3; i++) {
        var alive = slotIsAlive(i);
        if (notifierRouter.wasAlive[i] && !alive) {
            var idx = notifierRouter.slotOrder.indexOf(i);
            if (idx >= 0) notifierRouter.slotOrder.splice(idx, 1);
            clearSlotGroup(i);
            console.log('[Stack] slot ' + i + ' freed');
        }
        notifierRouter.wasAlive[i] = alive;
    }
}

/* RegisterActiveSlot (แท้ บรรทัด 799-803): ใหม่ = ล่างสุดของ stack
   dance/coalesce ไม่ re-rank */
function registerActiveSlot(slotIdx, priority) {
    if (notifierRouter.slotOrder.indexOf(slotIdx) >= 0) return;
    if (priority) notifierRouter.slotOrder.unshift(slotIdx);
    else notifierRouter.slotOrder.push(slotIdx);
    notifierRouter.wasAlive[slotIdx] = true;
}

function stackPitch(unit) {
    return (unit && unit.height || NOTIFIER_CONST.STACK_PITCH_PX - 10) + 10;
}

/* AssignUnitY: stack container วางที่ baseY=105px แล้ว; คืน child offset = rank × pitch
   = toast ใหม่เข้าที่ "ล่างสุดของ stack" เสมอ — เรียกก่อน createUnit
   แล้วส่งค่า topY เข้าไปตอนสร้าง (form แท้ set UnitTargetY ก่อน Show)        */
function assignUnitY(slotIdx) {
    var rank = notifierRouter.slotOrder.indexOf(slotIdx);
    if (rank < 0) rank = notifierRouter.slotOrder.length;
    var y = 0;
    for (var i = 0; i < rank; i++) {
        y += stackPitch(notifierRouter.slots[notifierRouter.slotOrder[i]]);
    }
    console.log('[Stack] slot ' + slotIdx + ' assigned stack offset Y=' + y +
                ' (rank ' + notifierRouter.slotOrder.length + ')');
    return y;
}

/* CompactStack (แท้ บรรทัด 823-843): ทุก active unit glide ไป baseY + rank × pitch
   — idempotent/self-heal (รันทุก heartbeat แก้ drift เอง); ข้าม unit ที่กำลัง
   transition — tick ถัดไปตามเก็บ */
function compactStack() {
    for (var rank = 0; rank < notifierRouter.slotOrder.length; rank++) {
        var slotIdx = notifierRouter.slotOrder[rank];
        var unit = notifierRouter.slots[slotIdx];
        if (!unit) continue;
        var target = 0;
        for (var i = 0; i < rank; i++) {
            target += stackPitch(notifierRouter.slots[notifierRouter.slotOrder[i]]);
        }
        if (unit.inTransition) continue;
        if (unit.top !== target) {
            unit.top = target;
            unit.el.style.top = target + 'px';   /* CSS transition 250ms ทำ glide */
        }
    }
}

/* RaiseVisibleStack (แท้ บรรทัด 900-910): เว็บไม่ต้อง — browser จัด z-index
   ด้วย .nv-notifier-stack z-index เดียว (หมายเหตุไว้ใน port-map) */

/* ---- toast unit lifecycle (แทนการจัดการ form) ------------------------------ */

function createUnit(slotIdx, entry, message, topY, group) {
    var stack = notifierEnsureStack();
    var el = document.createElement('div');
    el.className = 'nv-toast';
    if (entry.positive) el.className += ' is-positive';
    if (entry.width) el.className += ' is-gpu';
    if (entry.size) el.className += ' ' + entry.size;   /* NvGraphics: ขนาดพิเศษ (is-gpu-large) */
    if (entry.width) el.style.setProperty('--gpu-w', entry.width + 'px');
    el.style.top = topY + 'px';          /* ตำแหน่ง stack — glide ด้วย transition top */

    /* โครงตาม template-009 แท้:
       notifier-green (ชั้นหลัง) + notifier-black (ชั้นหน้า border-left เขียวในตัว)
       ข้างในดำ = .notifier (content) → icon + text-container(text + subtext)      */
    var green = document.createElement('div');
    green.className = 'nv-toast__green md-shadow-bottom-z-2';

    var black = document.createElement('div');
    black.className = 'nv-toast__black';

    var content = document.createElement('div');
    content.className = 'nv-toast__content';

    var icon = document.createElement('span');
    icon.className = 'nv-toast__icon share-icon' + (entry.iconClass ? ' ' + entry.iconClass : '');
    icon.textContent = entry.icon || '';

    var iconImage = document.createElement('img');
    iconImage.className = 'nv-toast__icon-image';
    iconImage.alt = '';
    if (entry.iconPath) {
        iconImage.src = entry.iconPath;
    }

    var textContainer = document.createElement('div');
    textContainer.className = 'nv-toast__text-container';

    var text = document.createElement('div');
    text.className = 'nv-toast__text';
    text.textContent = message;

    var subtext = document.createElement('div');
    subtext.className = 'nv-toast__subtext';
    subtext.style.display = 'none';

    textContainer.appendChild(text);
    textContainer.appendChild(subtext);
    content.appendChild(icon);
    content.appendChild(iconImage);
    content.appendChild(textContainer);
    var actionButton = document.createElement('button');
    actionButton.className = 'nv-toast__action';
    actionButton.type = 'button';
    actionButton.addEventListener('click', function (event) {
        event.stopPropagation();
        if (unit && unit.action && typeof unit.action.run === 'function')
            unit.action.run();
    });
    content.appendChild(actionButton);
    black.appendChild(content);
    el.appendChild(green);
    el.appendChild(black);
    stack.appendChild(el);

    /* click = ปิด (แทน bug F-4: Application.Restart ของ WinForms) */
    el.addEventListener('click', function () {
        destroyUnit(slotIdx);
    });

    var unit = {
        slot: slotIdx,
        el: el,
        group: group || '',
        height: entry.size === 'is-large' ? 124 : entry.size === 'is-tall' ? 150 : 90,
        steady: false,          /* = การ์ดจอด + เนื้อหาโผล่แล้ว (สายตา OWNER) */
        inTransition: false,    /* = _inTransition (exit กำลังวิ่ง) */
        dancing: false,         /* = _isDancing (dance กำลังวิ่ง) */
        top: topY,
        targetY: topY,
        autocloseTimer: null,
        revealTimer: null,
        destroying: false,
        pendingDanceUpdate: null,
        textEl: text,
        subtextEl: subtext,
        iconEl: icon,
        iconImageEl: iconImage,
        actionEl: actionButton,
        action: null,
        onClosed: null
    };
    iconImage.addEventListener('error', function () {
        if (!unit.fallbackIcon) return;
        iconImage.removeAttribute('src');
        iconImage.style.display = 'none';
        icon.textContent = unit.fallbackIcon;
        icon.style.display = '';
    });
    notifierRouter.slots[slotIdx] = unit;
    paintUnit(unit, message, entry);
    return unit;
}

/* paintSub ของแท้ (OscNotifierController d()): icon class + message + subtext */
function paintUnit(unit, message, entry, subtextMessage) {
    subtextMessage = subtextMessage || (entry && entry.subtext);
    if (entry && typeof entry.onClosed === 'function') unit.onClosed = entry.onClosed;
    unit.el.classList.remove('is-large', 'is-tall', 'is-gpu');
    if (entry && entry.size) unit.el.classList.add(entry.size);
    if (entry && Number.isFinite(entry.width) && entry.width >= 380) {
        unit.el.classList.add('is-gpu');
        unit.el.style.setProperty('--gpu-w', entry.width + 'px');
    } else {
        unit.el.style.removeProperty('--gpu-w');
    }
    unit.height = entry && entry.size === 'is-large' ? 124 :
        entry && entry.size === 'is-tall' ? 150 : 90;
    var iconClass = entry && entry.iconClass ? ' ' + entry.iconClass : '';
    unit.iconEl.className = 'nv-toast__icon share-icon' + iconClass +
        (entry && entry.positive ? ' is-highlighted' : '');
    unit.fallbackIcon = entry && entry.fallbackIcon || '';
    if (entry && entry.iconPath) {
        unit.iconEl.textContent = '';
        unit.iconImageEl.src = entry.iconPath;
        unit.iconImageEl.style.display = '';
        unit.iconEl.style.display = 'none';
    } else {
        unit.iconImageEl.removeAttribute('src');
        unit.iconImageEl.style.display = 'none';
        unit.iconEl.textContent = unit.fallbackIcon || (entry && entry.icon) || '';
        unit.iconEl.style.display = '';
    }
    unit.action = entry && entry.action ? entry.action : null;
    if (unit.action && unit.action.label) {
        unit.actionEl.textContent = unit.action.label;
        unit.actionEl.style.display = 'inline-flex';
    } else {
        unit.actionEl.textContent = '';
        unit.actionEl.style.display = 'none';
    }
    unit.textEl.textContent = message;
    if (subtextMessage) {
        unit.subtextEl.textContent = subtextMessage;
        unit.subtextEl.style.display = '';
    } else {
        unit.subtextEl.style.display = 'none';
    }
}

function destroyUnit(slotIdx) {
    var unit = notifierRouter.slots[slotIdx];
    if (!unit || unit.destroying) return;
    unit.destroying = true;
    if (unit.autocloseTimer) clearTimeout(unit.autocloseTimer);
    if (unit.revealTimer) clearTimeout(unit.revealTimer);
    unit.el.classList.remove('is-open', 'is-parked');
    unit.el.classList.add('is-leaving');
    var el = unit.el;
    setTimeout(function () {
        if (el && el.parentNode) el.parentNode.removeChild(el);
        if (notifierRouter.slots[slotIdx] === unit)
            notifierRouter.slots[slotIdx] = undefined;
        notifyUnitClosed(unit);
        updateSlotLiveness();
        compactStack();
        syncNativeNotifierRects();
    }, NOTIFIER_CONST.EXIT_MS);
}

function notifyUnitClosed(unit) {
    if (typeof unit.onClosed !== 'function') return;
    var onClosed = unit.onClosed;
    unit.onClosed = null;
    onClosed();
}

/* ============================================================================
   SECTION 6 — CHOREOGRAPHY — CSS-driven ตามแท้ทั้งหมด
   ----------------------------------------------------------------------------
   CSS แท้จัดการ delay ทุกชั้นเอง:
     ปิด (ค่าปกติ): ดำ delay 0s · เขียว delay .25s
     เปิด (is-open): เขียว delay 0s · ดำ delay .25s · เนื้อหา opacity .5s+delay .5s
   JS ทำแค่: สลับ class is-open → ลำดับเข้า/ออกถูกต้องโดยอัตโนมัติ
   (เข้า: เขียว→ดำ→เนื้อหา · ออก: ดำ→เขียว — สลับกันตาม delay แท้)
   ========================================================================== */

function choreographEntrance(unit) {
    var el = unit.el;
    void el.offsetWidth;                     /* reflow — ให้ transition เล่นจากจุดเริ่ม */
    el.classList.add('is-open');             /* แท้: .notifier-open — delay สลับ วิ่งเข้า */
    syncNativeNotifierRects();

    /* steady เมื่อเนื้อหาโผล่ครบ (500ms + delay 500ms); เริ่ม auto-close หลังจากนั้น */
    unit.autocloseTimer = setTimeout(function () {
        unit.steady = true;
        /* แท้ไม่มี auto-close ใน osc (หน้าคุมเอง) — ตัวนี้มาจาก WinForms คงไว้ */
        unit.autocloseTimer = setTimeout(function () {
            slideOutAll(unit);
        }, NOTIFIER_CONST.AUTOCLOSE_MS);
    }, 1000);
}

/* ออก (แท้: ถอด .notifier-open → delay กลับเป็น ดำ 0s/เขียว .25s = ดำออกก่อน)
   + reset สถานะ steady/dance                                                               */
function slideOutAll(unit) {
    unit.inTransition = true;
    unit.dancing = false;
    unit.steady = false;
    unit.el.classList.remove('is-open');
    /* transitionend ของชั้นเขียว (delay .25s ตามสถานะปิด) = ทุกชั้นออกครบ */
    var greenEl = unit.el.querySelector('.nv-toast__green');
    var onAllOut = function (event) {
        if (event.propertyName !== 'right' || event.target !== greenEl) return;
        greenEl.removeEventListener('transitionend', onAllOut);
        destroyUnit(unit.slot);
    };
    greenEl.addEventListener('transitionend', onAllOut);
    /* safety: ถ้า transitionend ไม่ยิง (display:none ฯลฯ) — ลบหลัง 1s */
    setTimeout(function () {
        if (notifierRouter.slots[unit.slot] === unit) destroyUnit(unit.slot);
    }, 1000);
}

/* Dance (Loader.vb ShowOnMain/DanceSideToast — OWNER T30.3):
   black.Left 0→Width+300 (600ms); green stays parked; rider hides.
   At 200ms replace the black animation from Left=Width, then return
   to Left=0 in 300ms before revealing the rider. */
function beginDance(unit) {
    if (unit.inTransition) return false;
    unit.inTransition = true;
    unit.dancing = true;
    unit.steady = false;
    unit.el.classList.remove('is-parked');   /* rider ซ่อน + strip หาย (BeginDance แท้) */
    return true;
}

function endDance(unit) {
    unit.inTransition = false;
    unit.dancing = false;
    unit.steady = true;                      /* = green_stop.Visible=True */
}

function danceToast(unit, message, entry, group) {
    /* WinForms T30.3: animate black only; green stays parked. */
    if (unit.autocloseTimer) clearTimeout(unit.autocloseTimer);

    if (unit.dancing) {
        unit.group = group || unit.group;
        unit.pendingDanceUpdate = { message: message, entry: entry };
        paintUnit(unit, message, entry);      /* coalesce — latest wins (T29.4) */
        return;
    }
    if (unit.inTransition) {
        paintUnit(unit, message, entry);      /* mid-exit: ห้ามวาดบนศพ — รอ displace path */
        return;
    }
    unit.group = group || unit.group;
    if (!beginDance(unit)) {
        paintUnit(unit, message, entry);
        return;
    }
    unit.pendingDanceUpdate = { message: message, entry: entry };

    var black = unit.el.querySelector('.nv-toast__black');
    var content = unit.el.querySelector('.nv-toast__content');

    if (unit.revealTimer) {
        clearTimeout(unit.revealTimer);
        unit.revealTimer = null;
    }

    var offstageRight = -(unit.el.getBoundingClientRect().width + 30);

    /* WinForms StartSlide: black panel fully exits before its rider is replaced. */
    black.style.transition = 'right ' + NOTIFIER_CONST.DANCE_OUT_MS + 'ms cubic-bezier(0.215, 0.61, 0.355, 1)';
    black.style.right = offstageRight + 'px';
    if (content) { content.style.transition = 'none'; content.style.opacity = '0'; }

    setTimeout(function () {
        /* Keep the card fully offstage while swapping text/icon, then slide it back. */
        black.style.transition = 'none';
        black.style.right = offstageRight + 'px';
        void black.offsetWidth;
        var pending = unit.pendingDanceUpdate || { message: message, entry: entry };
        unit.pendingDanceUpdate = null;
        paintUnit(unit, pending.message, pending.entry);

        black.style.transition = 'right ' + NOTIFIER_CONST.DANCE_IN_MS + 'ms cubic-bezier(0.215, 0.61, 0.355, 1)';
        black.style.right = '0px';
        setTimeout(function () {
            if (content) {
                content.style.transition = 'opacity ' + NOTIFIER_CONST.RIDER_FADE_MS + 'ms';
                content.style.opacity = '1';
            }
            black.style.transition = '';
            black.style.right = '';
            endDance(unit);
            unit.pendingDanceUpdate = null;
            unit.revealTimer = setTimeout(function () {
                if (unit.dancing) return;
                if (content) {
                    content.style.transition = '';
                    content.style.opacity = '';
                }
                unit.revealTimer = null;
            }, NOTIFIER_CONST.RIDER_FADE_MS);
            unit.autocloseTimer = setTimeout(function () {
                slideOutAll(unit);
            }, NOTIFIER_CONST.AUTOCLOSE_MS);
        }, NOTIFIER_CONST.DANCE_IN_MS);
    }, NOTIFIER_CONST.DANCE_TURNAROUND_MS);
}

/* ============================================================================
   SECTION 7 — UpdateNotifier (Loader.vb บรรทัด 497-604 — ทุกกฎเรียงตามแท้)
   ----------------------------------------------------------------------------
   message ต้อง localize แล้ว (เรียกผ่าน handleNotificationMessage)
   entry: { key, icon, positive } จาก registry
   ========================================================================== */

function updateNotifier(message, entry, group) {
    updateSlotLiveness();                    /* reconcile ก่อนตัดสินใจ (แท้บรรทัด 508) */
    var slotCount = configuredSlotCount();   /* อ่านใหม่ทุก toast (แท้บรรทัด 510) */

    if (entry.priority) {
        if (entry.updaterUi) {
            for (var updateSlot = 1; updateSlot <= slotCount; updateSlot++) {
                var updateUnit = notifierRouter.slots[updateSlot];
                if (!updateUnit || !sameToastGroup(updateUnit.group, group)) continue;
                if (!updateUnit.inTransition || updateUnit.dancing) {
                    danceToast(updateUnit, message, entry, group);
                    return;
                }
            }
        }
        for (var freeSlot = 1; freeSlot <= slotCount; freeSlot++) {
            if (!notifierRouter.slots[freeSlot]) {
                registerActiveSlot(freeSlot, true);
                var priorityUnit = createUnit(
                    freeSlot, entry, message, assignUnitY(freeSlot), group);
                choreographEntrance(priorityUnit);
                return;
            }
        }
        for (var replaceSlot = 1; replaceSlot <= slotCount; replaceSlot++) {
            var replaceUnit = notifierRouter.slots[replaceSlot];
            if (replaceUnit && replaceUnit.steady && !replaceUnit.inTransition &&
                !isProtectedToastGroup(replaceUnit.group)) {
                var orderIndex = notifierRouter.slotOrder.indexOf(replaceSlot);
                if (orderIndex > 0) {
                    notifierRouter.slotOrder.splice(orderIndex, 1);
                    notifierRouter.slotOrder.unshift(replaceSlot);
                }
                compactStack();
                danceToast(replaceUnit, message, entry, group);
                return;
            }
        }
        console.log('[Stack] no free or replaceable slot for priority toast: ' + group);
        return;
    }

    /* ---- กฎ 1: group live บน side slot + steady → dance ใน slot นั้น ---- */
    if (slotCount >= 2 && notifierRouter.slots[2] && notifierRouter.slots[2].steady &&
        sameToastGroup(notifierRouter.slots[2].group, group)) {
        danceToast(notifierRouter.slots[2], message, entry, group);
        return;
    }
    if (slotCount >= 3 && notifierRouter.slots[3] && notifierRouter.slots[3].steady &&
        sameToastGroup(notifierRouter.slots[3].group, group)) {
        danceToast(notifierRouter.slots[3], message, entry, group);
        return;
    }

    var groupLiveSomewhere =
        sameToastGroup((notifierRouter.slots[1] || {}).group, group) ||
        (slotCount >= 2 && sameToastGroup((notifierRouter.slots[2] || {}).group, group)) ||
        (slotCount >= 3 && sameToastGroup((notifierRouter.slots[3] || {}).group, group));

    /* ---- กฎ 2: main busy + group ใหม่ → side slot ว่างแรก / displace ---- */
    var mainBusy = !!notifierRouter.slots[1];
    if (mainBusy && !groupLiveSomewhere) {
        if (slotCount >= 2 && !notifierRouter.slots[2]) {
            registerActiveSlot(2);
            var u2 = createUnit(2, entry, message, assignUnitY(2), group);
            choreographEntrance(u2);
            return;
        }
        if (slotCount >= 3 && !notifierRouter.slots[3]) {
            registerActiveSlot(3);
            var u3 = createUnit(3, entry, message, assignUnitY(3), group);
            choreographEntrance(u3);
            return;
        }
        displaceFirstNormalSlot(message, entry, group);
        return;
    }

    /* ---- กฎ 2b: group live บน side slot แต่ไม่ steady ----
        mid-exit → displace (อย่าวาดบนศพ T29.4) · อื่น → dance/coalesce */
    if (slotCount >= 2 && notifierRouter.slots[2] && sameToastGroup(notifierRouter.slots[2].group, group)) {
        var s2 = notifierRouter.slots[2];
        if (s2.inTransition && !s2.dancing) {
            displaceFirstNormalSlot(message, entry, group);
        } else {
            danceToast(s2, message, entry, group);
        }
        return;
    }
    if (slotCount >= 3 && notifierRouter.slots[3] && sameToastGroup(notifierRouter.slots[3].group, group)) {
        var s3 = notifierRouter.slots[3];
        if (s3.inTransition && !s3.dancing) {
            displaceFirstNormalSlot(message, entry, group);
        } else {
            danceToast(s3, message, entry, group);
        }
        return;
    }

    /* ---- กฎ 3: main slot ---- */
    var main = notifierRouter.slots[1];
    if (main && main.inTransition && !main.dancing) {
        displaceFirstNormalSlot(message, entry, group);   /* mid-exit → ห้ามวาดบนศพ */
        return;
    }
    if (!main) {
        /* fresh show — ล่างสุดของ stack (แท้: AssignUnitY(1) + ShowOnMain fresh) */
        registerActiveSlot(1);
        var u1 = createUnit(1, entry, message, assignUnitY(1), group);
        choreographEntrance(u1);
        return;
    }
    /* main มีชีวิตอยู่ → dance หรือ coalesce ถ้ากำลัง dance */
    if (main.steady || main.dancing) {
        danceToast(main, message, entry, group);
    } else {
        paintUnit(main, message, entry);   /* mid-entrance: เนื้อหาใหม่วิ่งตาม entrance ที่กำลังเล่น */
    }
}

/* DisplaceFirstNormalSlot (Loader.vb บรรทัด 866-895 — no-queue rule):
   toast ที่หา slot ไม่ได้ "ห้าม drop" — ไปแทน slot แรกจากบนลงล่าง
   (main → side2 → side3) ที่ steady และไม่ใช่ toggle group
   fallback: ทุก slot ถือ toggle → dance บน main (main mid-exit = drop + log) */
function displaceFirstNormalSlot(message, entry, group) {
    var slotCount = configuredSlotCount();
    var main = notifierRouter.slots[1];

    if (main && main.steady && !main.inTransition && !isToggleGroup(main.group) &&
        !isProtectedToastGroup(main.group)) {
        danceToast(main, message, entry, group);
        return;
    }
    if (slotCount >= 2 && notifierRouter.slots[2] && notifierRouter.slots[2].steady &&
        !notifierRouter.slots[2].inTransition && !isToggleGroup(notifierRouter.slots[2].group) &&
        !isProtectedToastGroup(notifierRouter.slots[2].group)) {
        danceToast(notifierRouter.slots[2], message, entry, group);
        return;
    }
    if (slotCount >= 3 && notifierRouter.slots[3] && notifierRouter.slots[3].steady &&
        !notifierRouter.slots[3].inTransition && !isToggleGroup(notifierRouter.slots[3].group) &&
        !isProtectedToastGroup(notifierRouter.slots[3].group)) {
        danceToast(notifierRouter.slots[3], message, entry, group);
        return;
    }
    /* fallback: ทุก slot ถือ toggle group */
    if (main && main.inTransition && !main.dancing) {
        console.log('[Stack] no normal slot + main closing — dropped (no queue): ' + group);
        return;   /* ของแท้ drop จริง (no queue — OWNER rule) */
    }
    if (main && isProtectedToastGroup(main.group)) {
        console.log('[Stack] protected GPU toast retained; dropped incoming toast: ' + group);
        return;
    }
    console.log('[Stack] all slots toggle — displaced onto main: ' + group);
    danceToast(main, message, entry, group);
}

/* ============================================================================
   SECTION 8 — MESSAGE FLOW — port ตรงจาก OnMessage (Client.vb บรรทัด 100-157)
   ----------------------------------------------------------------------------
   "[NVIDIA Overlay]|l10n.recording_started" → parse → registry → localize →
   special-case InstantReplaySaved (duration args) → gate → updateNotifier
   ========================================================================== */

function handleNotificationMessage(rawMessage, argsOverride, presentationOverride) {
    var key = rawMessage;
    if (String(rawMessage).indexOf('|') >= 0) {
        key = String(rawMessage).split('|')[1].trim().replace(/"/g, '');
    }
    if (!key) return null;

    /* lookup (OrdinalIgnoreCase ตามแท้) */
    var entry = NOTIFICATION_REGISTRY[key];
    var registryKey = key;
    if (!entry) {
        for (var regKey in NOTIFICATION_REGISTRY) {
            if (regKey.toLowerCase() === key.toLowerCase()) {
                entry = NOTIFICATION_REGISTRY[regKey];
                registryKey = regKey;
                break;
            }
        }
    }
    if (!entry) {
        console.log('[notifier] Unknown key: ' + key);
        return null;
    }

    if (registryKey.toLowerCase() === 'l10n.notificationopenshare' &&
        typeof argsOverride === 'undefined') {
        return getOpenShareShortcut().then(function (shortcut) {
            return handleNotificationMessage(rawMessage, [shortcut], presentationOverride);
        }, function (error) {
            console.error('[notifier] Could not resolve the OpenShare shortcut', error);
            return null;
        });
    }

    var locKey = entry.locKey || entry.key || registryKey;
    var message;
    var args = argsOverride || entry.args || [];

    /* กรณีพิเศษ InstantReplaySaved / saved_last_15: duration มาจาก plane
       (แท้: อ่าน .m/.s files + ffprobe — เว็บให้ plane ส่ง args [mins, secs]) */
    if (registryKey === 'l10n.notificationInstantReplaySaved' ||
        registryKey === 'l10n.saved_last_15') {
        message = getNotificationText(locKey, [args[0] || '0', args[1] || '0']);
    } else {
        message = getNotificationText(locKey, args);
    }

    /* gate (Settings → Notifications) — fail-open */
    if (!notificationAllowed(registryKey)) {
        console.log('[notifier] gated: ' + registryKey);
        return null;
    }

    var group = notificationGroup(registryKey);
    return updateNotifier(message, {
        key: registryKey,
        icon: entry.icon || '',
        positive: !!entry.positive,
        iconPath: presentationOverride && presentationOverride.iconPath || '',
        fallbackIcon: presentationOverride && presentationOverride.fallbackIcon || ''
    }, group);
}

/* ============================================================================
   SECTION 9 — PUBLIC API
   ============================================================================
   Notifier.fire(key, args?)        → flow เต็ม (registry→localize→gate→router)
   Notifier.show(message, opts)     → ยิงตรง (bypass registry — สำหรับ test)
   Notifier.setGates({...})         → ใส่ค่า 27 gates
   Notifier.setSlotCount(n)         → 1-3
   ========================================================================== */

var Notifier = {
    fire: function (key, args, presentation) {
        return handleNotificationMessage(key, args, presentation);
    },
    getOpenShareShortcut: getOpenShareShortcut,
    show: function (message, opts) {
        opts = opts || {};
        return updateNotifier(message, {
            key: opts.key || '',
            icon: opts.icon || '',
            iconClass: opts.iconClass || '',
            iconPath: opts.iconPath || '',
            subtext: opts.subtext || '',
            width: Number.isFinite(opts.width) && opts.width >= 380 ? opts.width : 0,
            positive: !!opts.positive,
            action: opts.action || null,
            size: opts.size || '',
            priority: !!opts.priority,
            updaterUi: !!opts.updaterUi,
            fallbackIcon: opts.fallbackIcon || '',
            onClosed: typeof opts.onClosed === 'function' ? opts.onClosed : null
        },
                              opts.group || ('raw:' + message));
    },
    setGates: function (gates) {
        for (var g in gates) NOTIFIER_CONFIG.gates[g] = gates[g];
    },
    setSlotCount: function (n) { NOTIFIER_CONFIG.slotCount = n; },
    status: function () {
        var out = [];
        for (var i = 1; i <= 3; i++) {
            var u = notifierRouter.slots[i];
            out.push('slot ' + i + ': ' + (u ? (u.group || '(no group)') + (u.steady ? ' [steady]' : ' [animating]') : 'free'));
        }
        return out.join(' · ');
    },
    hasActiveGroupPrefix: function (prefix) {
        prefix = String(prefix || '').toLowerCase();
        if (!prefix) return false;
        for (var i = 1; i <= 3; i++) {
            var unit = notifierRouter.slots[i];
            if (unit && String(unit.group || '').toLowerCase().indexOf(prefix) === 0)
                return true;
        }
        return false;
    },
    /* StartStackHeartbeat (แท้ บรรทัด 740-747): ทุก 100ms —
       UpdateSlotLiveness (slot ตายออกจาก stack) + CompactStack (glide ปิดช่องว่าง)
       idempotent/self-heal — แก้ drift ทุกอย่างเองภายใน 1 tick                  */
    startHeartbeat: function () {
        if (Notifier._heartbeat) return;
        Notifier._heartbeat = setInterval(function () {
            try {
                updateSlotLiveness();
                compactStack();
                syncNativeNotifierRects();
            } catch (err) {
                console.log('[Stack] heartbeat error: ' + err.message);  /* แท้: ห้ามให้ heartbeat ฆ่าแอป */
            }
        }, NOTIFIER_CONST.STACK_HEARTBEAT_MS);
        console.log('[Stack] heartbeat started (100ms)');
    }
};
