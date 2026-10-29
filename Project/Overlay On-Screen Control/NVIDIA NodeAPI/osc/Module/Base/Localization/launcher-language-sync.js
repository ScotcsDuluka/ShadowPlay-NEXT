/* Launcher -> OSC locale bridge for the shared static language state.
 * Launcher is authoritative when it publishes a NEW choice. A manual OSC
 * language change must not be overwritten every 3s by an unchanged file.
 * No game or overlay runtime control is performed here.
 */
(function (window) {
  'use strict';
  if (!window.angular || !window.fetch) return;
  window.angular.module('main').run(['$translate', '$log', '$rootScope',
    function ($translate, $log, $rootScope) {
      var fileUrl = '/launcher-language.json';
      var lastPublished = '';
      var busy = false;
      var nextLanguage = '';
      var validLocale = /^[a-z]{2}-[A-Z]{2,3}$/;

      function applyLocale(lang) {
        if (!validLocale.test(lang) || nextLanguage === lang) return;
        if ($translate.use() === lang) {
          document.documentElement.lang = lang.slice(0, 2);
          return;
        }
        nextLanguage = lang;
        Promise.resolve($translate.use(lang)).then(function () {
          document.documentElement.lang = lang.slice(0, 2);
          $log.info('[launcher-language] OSC switched to ' + lang);
        }, function (error) {
          $log.warn('[launcher-language] unavailable language ' + lang, error);
        }).then(function () { nextLanguage = ''; });
      }

      // Keep document language correct for changes originating inside OSC.
      if ($rootScope && $rootScope.$on) {
        $rootScope.$on('$translateChangeSuccess', function (event, data) {
          var active = (data && data.language) || $translate.use();
          if (validLocale.test(active)) document.documentElement.lang = active.slice(0, 2);
        });
      }

      function checkLanguage() {
        if (busy) return;
        busy = true;
        window.fetch(fileUrl, { cache: 'no-store' }).then(function (response) {
          if (!response.ok) return null;
          return response.json();
        }).then(function (payload) {
          var lang = payload && payload.language;
          if (typeof lang !== 'string' || !validLocale.test(lang)) return;
          // Only a changed Launcher publication should override a manual
          // change in OSC; otherwise an unchanged file wins every 3 seconds.
          if (lang !== lastPublished) {
            lastPublished = lang;
            applyLocale(lang);
          }
        }).catch(function (error) {
          $log.warn('[launcher-language] unable to read language state', error);
        }).then(function () { busy = false; });
      }

      window.setTimeout(checkLanguage, 1500);
      window.setInterval(checkLanguage, 3000);
    }
  ]);
})(window);
