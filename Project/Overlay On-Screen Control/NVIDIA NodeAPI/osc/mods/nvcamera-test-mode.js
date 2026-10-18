(function (angular) {
  'use strict';

  if (!window.NV_CONTAINER_TEST_MODE || !angular) return;

  angular.module('main').config(['$provide', function ($provide) {
    function runWithOfflineAvailability(invoke, context, args, $injector) {
        var oscService = $injector.get('oscService');
        var wasOnline = oscService.onlineState.online;
        if (wasOnline !== true) return invoke.apply(context, args);

        var $rootScope = $injector.get('$rootScope');
        var $timeout = $injector.get('$timeout');
        var restored = false;
        var removeRouteListener;
        var restoreTimeout;

        function restoreOnlineState() {
          if (restored) return;
          restored = true;
          if (removeRouteListener) removeRouteListener();
          if (restoreTimeout) $timeout.cancel(restoreTimeout);
          oscService.setOnline(wasOnline);
        }

        removeRouteListener = $rootScope.$on('$stateChangeSuccess', function (event, toState) {
          if (toState && /(?:^|\.)(?:mods|nvcamera)$/.test(toState.name)) {
            restoreOnlineState();
          }
        });
        restoreTimeout = $timeout(restoreOnlineState, 15000, false);
        oscService.setOnline(false);

        var result;
        try {
          result = invoke.apply(context, args);
        } catch (error) {
          restoreOnlineState();
          throw error;
        }
        if (result && typeof result.finally === 'function') result.finally(restoreOnlineState);
        return result;
    }

    $provide.decorator('eventAggregator', ['$delegate', '$injector', function ($delegate, $injector) {
      var trigger = $delegate.trigger;
      if (typeof trigger !== 'function') {
        throw new Error('eventAggregator.trigger is unavailable');
      }

      $delegate.trigger = function (eventName) {
        if (eventName === 'Hotkey_ModsShowUI') {
          return runWithOfflineAvailability(trigger, $delegate, arguments, $injector);
        }
        return trigger.apply($delegate, arguments);
      };
      return $delegate;
    }]);

    $provide.decorator('nvCameraService', ['$delegate', '$injector', function ($delegate, $injector) {
      var launchUIForMods = $delegate.launchUIForMods;
      if (typeof launchUIForMods !== 'function') {
        throw new Error('nvCameraService.launchUIForMods is unavailable');
      }

      $delegate.launchUIForMods = function () {
        return runWithOfflineAvailability(launchUIForMods, $delegate, arguments, $injector);
      };
      return $delegate;
    }]);
  }]);
})(window.angular);
