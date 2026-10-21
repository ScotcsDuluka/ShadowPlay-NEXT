/* OSC uses the locale explicitly selected in Launcher.Cef.
 * Same product language file is served by the already-running OSC static host.
 * This runs within the existing Angular Translate engine and never touches
 * the native game/overlay runtime.
 */
(function (window) {
  'use strict';
  if (!window.angular || !window.fetch) return;
  angular.module('main').run(['$translate', '$log', function ($translate, $log) {
    var fileUrl = '/mods/launcher-language.json';
    var lastApplied = '';
    var busy = false;
    var nextLanguage = '';
    var validLocale = /^[a-z]{2}-[A-Z]{2,3}$/;

    function applyLocale(lang) {
      if (!validLocale.test(lang)) return;
      if (lastApplied === lang && $translate.use() === lang) return;
      if (nextLanguage === lang) return;
      if ($translate.use() === lang) {
        lastApplied = lang;
        return;
      }
      nextLanguage = lang;
      Promise.resolve($translate.use(lang)).then(function () {
        lastApplied = lang;
        document.documentElement.lang = lang.slice(0, 2);
        $log.info('[launcher-language] OSC switched to ' + lang);
      }, function (error) {
        $log.warn('[launcher-language] unavailable language ' + lang, error);
      }).then(function () {
        nextLanguage = '';
      });
    }

    function checkLanguage() {
      if (busy) return;
      busy = true;
      fetch(fileUrl, { cache: 'no-store' }).then(function (response) {
        if (!response.ok) return null; // Launcher has not published a choice yet.
        return response.json();
      }).then(function (payload) {
        if (payload && typeof payload.language === 'string') applyLocale(payload.language);
      }).catch(function (error) {
        $log.warn('[launcher-language] unable to read language state', error);
      }).then(function () { busy = false; });
    }

    window.setTimeout(checkLanguage, 1500);
    window.setInterval(checkLanguage, 3000);
  }]);
})(window);
