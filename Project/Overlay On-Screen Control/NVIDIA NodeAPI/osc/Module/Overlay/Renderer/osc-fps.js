(function (window) {
  'use strict';
  if (!window.angular || window.__dulukaOscFpsInstalled) return;
  window.__dulukaOscFpsInstalled = true;

  var template = '<div class="osc-content" layout="row" ng-cloak>' +
    '<div class="osc-bread-crumb"><nv-osc-tile title="Overlay FPS" icon-font="icon-performance"></nv-osc-tile></div>' +
    '<div class="osc-central-div" layout="column"><div class="preferences-root-panel">' +
    '<div class="preferences-root-label"><p translate="l10n.oscOverlayFps">Overlay FPS</p></div>' +
    '<p class="preferences-instructional-text" translate="l10n.oscOverlayFpsInfo">Limit Desktop Overlay frame rate. Game FPS is unchanged.</p>' +
    '<md-input-container><label translate="l10n.oscOverlayFpsLimit">FPS limit</label>' +
    '<md-select class="osc-select resolution-fps-select" ng-model="fpsSettings.fps" ng-change="fpsSettings.save()" ng-disabled="!fpsSettings.loaded || fpsSettings.busy" aria-label="Overlay FPS limit" hover-focus>' +
    '<md-option md-ink-ripple="false" ng-repeat="value in fpsSettings.options track by value" ng-value="value" hover-focus>{{value}} FPS</md-option>' +
    '</md-select></md-input-container>' +
    '<p class="preferences-instructional-text" translate="l10n.oscOverlayFpsHint">Higher limits may use more power. Actual FPS can be lower than the selected limit.</p>' +
    '<p ng-if="fpsSettings.busy && !fpsSettings.loaded" role="status" translate="l10n.oscOverlayFpsLoading">Loading…</p>' +
    '<p ng-if="fpsSettings.busy && fpsSettings.loaded" role="status" translate="l10n.oscOverlayFpsSaving">Applying…</p>' +
    '<p ng-if="fpsSettings.saved && !fpsSettings.busy && !fpsSettings.error" role="status" translate="l10n.oscOverlayFpsSaved">Saved. Changes apply immediately.</p>' +
    '<p ng-if="fpsSettings.error" role="alert" translate="{{fpsSettings.error}}"></p>' +
    '<button ng-if="!fpsSettings.loaded && !fpsSettings.busy" class="osc-button osc-button-gray" ng-click="fpsSettings.load()" translate="l10n.oscOverlayFpsRetry">Retry</button>' +
    '</div></div><div class="osc-side-buttons" layout="column">' +
    '<button class="osc-button osc-button-gray" ng-click="fpsSettings.back()" translate="l10n.back" hover-focus>Back</button>' +
    '</div></div>';

  window.angular.module('main').config(['$stateProvider', function ($stateProvider) {
    $stateProvider.state('main.osc-fps', {
      url: '/preferences/overlay-fps',
      template: template,
      controller: 'OscFpsController',
      controllerAs: 'fpsSettings'
    });
  }]).controller('OscFpsController', ['$q', '$state', '$scope', function ($q, $state, $scope) {
    var vm = this, disposed = false;
    vm.options = [15, 30, 60, 75, 90, 120, 144, 165, 240];
    vm.fps = null;
    vm.loaded = false;
    vm.busy = false;
    vm.saved = false;
    vm.error = '';
    var persisted = null;
    $scope.$on('$destroy', function () { disposed = true; });

    function query(command, fps) {
      return $q(function (resolve, reject) {
        if (typeof window.cefQuery !== 'function') { reject(new Error('native bridge unavailable')); return; }
        var request = {command: command};
        if (fps !== undefined) request.fps = fps;
        try {
          window.cefQuery({
            request: JSON.stringify(request),
            onSuccess: function (raw) {
              try {
                var data = JSON.parse(raw);
                if (!data || data.error || typeof data.fps !== 'number' ||
                    data.fps % 1 !== 0 || data.fps < 15 || data.fps > 240) {
                  reject(new Error('invalid FPS response')); return;
                }
                resolve(data);
              } catch (error) { reject(error); }
            },
            onFailure: function (code, message) { reject(new Error(message || String(code))); }
          });
        } catch (error) { reject(error); }
      });
    }

    function accept(data) {
      persisted = data.fps;
      vm.fps = data.fps;
      if (vm.options.indexOf(data.fps) === -1) {
        vm.options.push(data.fps);
        vm.options.sort(function (a, b) { return a - b; });
      }
    }

    vm.load = function () {
      if (vm.busy || disposed) return;
      vm.busy = true; vm.error = '';
      return query('QUERY_OSC_GET_FPS').then(function (data) {
        if (disposed) return;
        accept(data); vm.loaded = true;
      }, function () {
        if (!disposed) vm.error = 'l10n.oscOverlayFpsLoadFailed';
      }).finally(function () { if (!disposed) vm.busy = false; });
    };
    vm.save = function () {
      if (!vm.loaded || vm.busy || disposed || vm.fps === persisted) return;
      if (typeof vm.fps !== 'number' || vm.fps % 1 !== 0 || vm.fps < 15 || vm.fps > 240) {
        vm.fps = persisted; vm.error = 'l10n.oscOverlayFpsSaveFailed'; return;
      }
      vm.busy = true; vm.saved = false; vm.error = '';
      return query('QUERY_OSC_SET_FPS', vm.fps).then(function (data) {
        if (disposed) return;
        accept(data); vm.saved = true;
      }, function () {
        if (disposed) return;
        vm.fps = persisted; vm.error = 'l10n.oscOverlayFpsSaveFailed';
      }).finally(function () { if (!disposed) vm.busy = false; });
    };
    vm.back = function () { return $state.go('main.main-menu'); };
    vm.load();
  }]);
})(window);
