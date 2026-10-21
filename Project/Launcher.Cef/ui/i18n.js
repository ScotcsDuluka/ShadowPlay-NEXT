/* Launcher-only localization: English is canonical; Thai is optional.
 * CEF 73 compatible (ES5 syntax). Never changes OSC locale or system locale.
 */
(function (window, document) {
  'use strict';

  var STORAGE_KEY = 'nv-launcher-language-v1';
  var COOKIE_KEY = 'nv_launcher_language';
  var DEFAULT_LANGUAGE = 'en-US';
  var messages = {
    'en-US': {
      launcher: 'LAUNCHER',
      minimize: 'Minimize',
      close: 'Close',
      suite: 'GAME RECORDING SUITE',
      subtitle: 'Capture Every Moment',
      description: 'Manage your overlay engine and monitor the services that power capture. Status and preferences are synchronized with the local system.',
      controller: 'CONTROLLER',
      presenter: 'PRESENTER',
      apiService: 'API SERVICE',
      controllerHint: 'Overlay controller (NvContainer.exe)',
      presenterHint: 'On-screen overlay presenter (NVIDIA OSC.exe)',
      apiHint: 'Local API service (engine dependent)',
      status: 'Status',
      services: 'SERVICES',
      monitoring: '● MONITORING',
      overlayService: 'SHADOWPLAY OVERLAY',
      overlayController: 'OVERLAY CONTROLLER',
      notifierService: 'WINFORM NOTIFIER',
      localApiService: 'LOCAL API SERVICE',
      stopped: 'STOPPED',
      loading: 'LOADING',
      running: 'RUNNING',
      openOverlay: 'OPEN OVERLAY',
      overlayInGame: 'Overlay In-game',
      featureDescription: 'Turn the selected overlay engine on or off.',
      overlayEnabled: 'Overlay enabled',
      engineTitle: 'OVERLAY ENGINE',
      overlayMode: 'Overlay engine mode',
      modeDescription: 'Selects the engine; does not power the overlay.',
      connecting: 'CONNECTING',
      bridgeLive: 'BRIDGE LIVE',
      bridgeOffline: 'BRIDGE OFFLINE',
      exitAll: 'EXIT ALL',
      sending: 'SENDING',
      sent: 'SENT',
      hubOffline: 'HUB OFFLINE',
      exiting: 'EXITING',
      clickAgain: 'CLICK AGAIN',
      settingSaved: 'SETTING SAVED',
      stateSyncFailed: 'STATE SYNC FAILED',
      saveFailed: 'COULD NOT SAVE SETTING',
      cefStarting: 'CEF ENGINE STARTING',
      winformSelected: 'WINFORM ENGINE SELECTED',
      changeEngineFailed: 'COULD NOT CHANGE ENGINE',
      overlayRequestSent: 'OVERLAY REQUEST SENT',
      overlayServiceOffline: 'OVERLAY SERVICE OFFLINE',
      overlayRequestFailed: 'OVERLAY REQUEST FAILED',
      openObt3Failed: 'COULD NOT OPEN OBT3',
      exitFailed: 'EXIT ALL FAILED',
      language: 'Language',
      languageSelect: 'Launcher language'
    },
    'th-TH': {
      launcher: 'ตัวเปิดโปรแกรม',
      minimize: 'ย่อหน้าต่าง',
      close: 'ปิดหน้าต่าง',
      suite: 'ชุดเครื่องมือบันทึกเกม',
      subtitle: 'เก็บทุกช่วงเวลาสำคัญ',
      description: 'จัดการระบบโอเวอร์เลย์และตรวจสอบบริการที่ใช้ในการบันทึกภาพ สถานะและการตั้งค่าจะซิงก์กับระบบในเครื่อง',
      controller: 'ตัวควบคุม',
      presenter: 'ตัวแสดงผล',
      apiService: 'บริการ API',
      controllerHint: 'ตัวควบคุมโอเวอร์เลย์ (NvContainer.exe)',
      presenterHint: 'ตัวแสดงผลโอเวอร์เลย์ (NVIDIA OSC.exe)',
      apiHint: 'บริการ API ภายในเครื่อง (ขึ้นกับเอนจิน)',
      status: 'สถานะ',
      services: 'บริการระบบ',
      monitoring: '● กำลังตรวจสอบ',
      overlayService: 'SHADOWPLAY OVERLAY',
      overlayController: 'ตัวควบคุมโอเวอร์เลย์',
      notifierService: 'ระบบแจ้งเตือน WINFORM',
      localApiService: 'บริการ API ภายในเครื่อง',
      stopped: 'หยุดทำงาน',
      loading: 'กำลังโหลด',
      running: 'กำลังทำงาน',
      openOverlay: 'เปิดโอเวอร์เลย์',
      overlayInGame: 'โอเวอร์เลย์ในเกม',
      featureDescription: 'เปิดหรือปิดระบบโอเวอร์เลย์ที่เลือก',
      overlayEnabled: 'เปิดใช้งานโอเวอร์เลย์',
      engineTitle: 'ระบบโอเวอร์เลย์',
      overlayMode: 'เลือกเอนจินโอเวอร์เลย์',
      modeDescription: 'เลือกเอนจินที่ใช้งาน โดยไม่เปลี่ยนสถานะเปิดหรือปิดโอเวอร์เลย์',
      connecting: 'กำลังเชื่อมต่อ',
      bridgeLive: 'เชื่อมต่อแล้ว',
      bridgeOffline: 'ไม่ได้เชื่อมต่อ',
      exitAll: 'ปิดทั้งหมด',
      sending: 'กำลังส่งคำสั่ง',
      sent: 'ส่งแล้ว',
      hubOffline: 'บริการไม่พร้อม',
      exiting: 'กำลังปิด',
      clickAgain: 'คลิกอีกครั้ง',
      settingSaved: 'บันทึกการตั้งค่าแล้ว',
      stateSyncFailed: 'ซิงก์สถานะไม่สำเร็จ',
      saveFailed: 'บันทึกการตั้งค่าไม่สำเร็จ',
      cefStarting: 'กำลังเริ่ม CEF',
      winformSelected: 'เลือกเอนจิน WINFORM แล้ว',
      changeEngineFailed: 'เปลี่ยนเอนจินไม่สำเร็จ',
      overlayRequestSent: 'ส่งคำสั่งเปิดโอเวอร์เลย์แล้ว',
      overlayServiceOffline: 'บริการโอเวอร์เลย์ไม่พร้อม',
      overlayRequestFailed: 'เปิดโอเวอร์เลย์ไม่สำเร็จ',
      openObt3Failed: 'เปิด OBT3 ไม่สำเร็จ',
      exitFailed: 'ปิดบริการทั้งหมดไม่สำเร็จ',
      language: 'ภาษา',
      languageSelect: 'เลือกภาษาของ Launcher'
    }
  };

  // Reuse the existing OSC locale names and suitable translated labels.
  // Launcher-specific messages not present in OSC fall back to English.
  var oscLocales = window.LauncherOscLocales || {};
  Object.keys(oscLocales).forEach(function (code) {
    if (!messages[code]) messages[code] = oscLocales[code].messages || {};
  });

  function populateLanguages() {
    var select = document.getElementById('launcherLanguage');
    if (!select) return;
    var codes = Object.keys(oscLocales).sort();
    for (var i = 0; i < codes.length; i++) {
      var code = codes[i];
      if (code === 'en-US' || code === 'th-TH') continue;
      var option = document.createElement('option');
      option.value = code;
      option.textContent = oscLocales[code].name || code;
      select.appendChild(option);
    }
  }

  function supported(lang) {
    return Object.prototype.hasOwnProperty.call(messages, lang);
  }

  // Cookies are scoped to the localhost host rather than its ephemeral port.
  // CEF keeps the launcher browser profile on disk across app restarts.
  function readCookie() {
    try {
      var parts = String(document.cookie || '').split(';');
      for (var i = 0; i < parts.length; i++) {
        var pair = parts[i].trim().split('=');
        if (pair[0] === COOKIE_KEY) return decodeURIComponent(pair.slice(1).join('='));
      }
    } catch (e) {}
    return '';
  }

  var language = DEFAULT_LANGUAGE;
  var cookieLanguage = readCookie();
  if (supported(cookieLanguage)) language = cookieLanguage;
  else try {
    var saved = window.localStorage.getItem(STORAGE_KEY);
    if (supported(saved)) language = saved;
  } catch (e) {}

  function t(key) {
    return (messages[language] && messages[language][key]) ||
      messages[DEFAULT_LANGUAGE][key] || key;
  }

  function apply() {
    document.documentElement.lang = language.slice(0, 2);
    var nodes = document.querySelectorAll('[data-i18n], [data-i18n-title], [data-i18n-aria-label]');
    for (var i = 0; i < nodes.length; i++) {
      var node = nodes[i];
      var key = node.getAttribute('data-i18n');
      if (key) node.textContent = t(key);
      key = node.getAttribute('data-i18n-title');
      if (key) node.setAttribute('title', t(key));
      key = node.getAttribute('data-i18n-aria-label');
      if (key) node.setAttribute('aria-label', t(key));
    }
    var select = document.getElementById('launcherLanguage');
    if (select) select.value = language;
  }

  function setLanguage(value) {
    if (!supported(value)) value = DEFAULT_LANGUAGE;
    language = value;
    try { window.localStorage.setItem(STORAGE_KEY, language); } catch (e) {}
    try {
      document.cookie = COOKIE_KEY + '=' + encodeURIComponent(language) +
        '; Max-Age=31536000; Path=/; SameSite=Lax';
    } catch (e) {}
    apply();
    if (typeof window.onLauncherLanguageChanged === 'function')
      window.onLauncherLanguageChanged(language);
    return language;
  }

  populateLanguages();

  window.LauncherI18n = {
    t: t,
    apply: apply,
    setLanguage: setLanguage,
    getLanguage: function () { return language; }
  };
})(window, document);
