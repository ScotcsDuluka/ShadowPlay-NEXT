(function(angular){'use strict';angular.module('main').config(['$stateProvider',function($stateProvider){$stateProvider.state('main.gpu-profiles',{url:'/preferences/game-profiles',template:'<div class="nv-gpu-profiles-route-slot"></div>'});}]).run(['$rootScope', '$http', '$state', function ($rootScope, $http, $state) {
    var checkingReadiness = false;
    var allowReadyTransition = false;
    function fullInterfaceEnabled() {
      return window.__dulukaOscInterface && window.__dulukaOscInterface.navigationEnabled;
    }
    $rootScope.$on('$stateChangeStart', function (event, toState, toParams) {
      if (toState.name !== 'main.gpu-profiles') return;
      if (fullInterfaceEnabled()) return;
      if (allowReadyTransition) {
        allowReadyTransition = false;
        return;
      }
      event.preventDefault();
      if (checkingReadiness) return;
      checkingReadiness = true;
      $http.get('/Duluka/v1/gpu/graphics/status').then(function (response) {
        var ready = response.data && response.data.ready === true;
        window.dispatchEvent(new CustomEvent('nvgraphics-readiness-change', {
          detail: { ready: ready }
        }));
        if (ready) {
          allowReadyTransition = true;
          return $state.go('main.gpu-profiles', toParams);
        }
        return $state.go('main.preferences');
      }, function () {
        window.dispatchEvent(new CustomEvent('nvgraphics-readiness-change', {
          detail: { ready: false }
        }));
        return $state.go('main.preferences');
      }).finally(function () {
        checkingReadiness = false;
      });
    });
    var readinessTimer = null;
    $rootScope.$on('$stateChangeSuccess', function (event, toState) {
      if (toState.name === 'main.gpu-profiles') {
        if (readinessTimer) window.clearInterval(readinessTimer);
        readinessTimer = window.setInterval(function () {
          $http.get('/Duluka/v1/gpu/graphics/status').then(function (response) {
            if (!response.data || response.data.ready !== true) {
              window.dispatchEvent(new CustomEvent('nvgraphics-readiness-change', {
                detail: { ready: false }
              }));
              if (!fullInterfaceEnabled()) $state.go('main.preferences');
            }
          }, function () {
            window.dispatchEvent(new CustomEvent('nvgraphics-readiness-change', {
              detail: { ready: false }
            }));
            if (!fullInterfaceEnabled()) $state.go('main.preferences');
          });
        }, 2000);
      } else if (readinessTimer) {
        window.clearInterval(readinessTimer);
        readinessTimer = null;
      }
    });
  }]);
})(window.angular);
