(function (window) {
  'use strict';
  if (!window.angular || window.__dulukaOscRendererInstalled) return;
  window.__dulukaOscRendererInstalled = true;

  var choices = ['auto', 'gpu', 'cpu'];
  var template = [
    '<div class="osc-content" layout="row" ng-cloak>',
    '<div class="osc-bread-crumb"><nv-osc-tile title="Renderer Mode" icon-font="icon-performance"></nv-osc-tile></div>',
    '<div class="osc-central-div" layout="column"><div class="preferences-root-panel">',
    '<div class="preferences-root-label"><p translate="l10n.oscRendererTitle">Renderer Mode</p></div>',
    '<p class="preferences-instructional-text" translate="l10n.oscRendererInfo">Select a rendering preference for each part of OSC.</p>',
    '<div ng-repeat="part in rendererSettings.parts track by part.key">',
    '<md-input-container><label translate="{{part.label}}">{{part.fallback}}</label>',
    '<md-select class="osc-select resolution-fps-select" ng-model="rendererSettings.values[part.key]" ng-change="rendererSettings.save()" ng-disabled="!rendererSettings.loaded || rendererSettings.busy" aria-label="{{part.fallback}}" hover-focus>',
    '<md-option md-ink-ripple="false" ng-repeat="choice in rendererSettings.choices track by choice.key" ng-value="choice.key" hover-focus><span translate="{{choice.label}}">{{choice.fallback}}</span></md-option>',
    '</md-select></md-input-container></div>',
    '<p class="preferences-instructional-text" translate="l10n.oscRendererBackgroundInfo">Background GPU uses WebGL particles when supported; CPU uses Canvas 2D. Both share the main CEF compositor.</p>',
    '<p ng-if="rendererSettings.restartRequired" role="status" translate="l10n.oscRendererRestart">Restart OSC to apply UI and Overlay Display changes.</p>',
    '<p ng-if="rendererSettings.saved && !rendererSettings.busy && !rendererSettings.error" role="status" translate="l10n.oscRendererSaved">Settings saved.</p>',
    '<p ng-if="rendererSettings.busy" role="status" translate="l10n.oscRendererWorking">Working…</p>',
    '<p ng-if="rendererSettings.error" role="alert" translate="{{rendererSettings.error}}"></p>',
    '<button ng-if="!rendererSettings.loaded && !rendererSettings.busy" class="osc-button osc-button-gray" ng-click="rendererSettings.load()" translate="l10n.oscOverlayFpsRetry">Retry</button>',
    '</div></div><div class="osc-side-buttons" layout="column">',
    '<button class="osc-button osc-button-gray" ng-click="rendererSettings.back()" translate="l10n.back" hover-focus>Back</button>',
    '</div></div>'
  ].join('');

  window.angular.module('main')
    .config(['$stateProvider', function ($stateProvider) {
      $stateProvider.state('main.osc-renderer', {
        url: '/preferences/overlay-renderer',
        template: template, controller: 'OscRendererController',
        controllerAs: 'rendererSettings'
      });
    }])
    .controller('OscRendererController', ['$q', '$state', '$scope', function ($q, $state, $scope) {
      var vm = this, disposed = false, persisted = null;
      vm.parts = [
        {key:'ui', label:'l10n.oscRendererUi', fallback:'Overlay UI'},
        {key:'background', label:'l10n.oscRendererBackground', fallback:'Background particles'},
        {key:'desktop', label:'l10n.oscRendererDesktop', fallback:'Overlay Display'}
      ];
      vm.choices = [
        {key:'auto', label:'l10n.oscRendererAuto', fallback:'Auto (recommended)'},
        {key:'gpu', label:'l10n.oscRendererGpu', fallback:'GPU'},
        {key:'cpu', label:'l10n.oscRendererCpu', fallback:'CPU'}
      ];
      vm.values = {ui:'auto', background:'auto', desktop:'auto'};
      vm.loaded = false; vm.busy = false; vm.error = ''; vm.saved = false;
      vm.restartRequired = false;
      $scope.$on('$destroy', function () { disposed = true; });

      function valid(data) {
        return !!data && choices.indexOf(data.ui) >= 0 &&
          choices.indexOf(data.desktop) >= 0 && choices.indexOf(data.background) >= 0;
      }
      function query(command, values) {
        return $q(function (resolve, reject) {
          if (typeof window.cefQuery !== 'function') {
            reject(new Error('Native bridge unavailable')); return;
          }
          var request = {command:command};
          if (values) {
            request.ui=values.ui; request.desktop=values.desktop; request.background=values.background;
          }
          try {
            window.cefQuery({
              request:JSON.stringify(request),
              onSuccess:function (raw) {
                try {
                  var data=JSON.parse(raw);
                  if (!valid(data) || data.error) { reject(new Error('Invalid renderer response')); return; }
                  resolve(data);
                } catch (error) { reject(error); }
              },
              onFailure:function (code, message) { reject(new Error(message || String(code))); }
            });
          } catch (error) { reject(error); }
        });
      }
      function accept(data) {
        persisted={ui:data.ui, desktop:data.desktop, background:data.background};
        vm.values={ui:data.ui, desktop:data.desktop, background:data.background};
        vm.restartRequired=!!data.restartRequired;
        if (window.__dulukaOscBackground && typeof window.__dulukaOscBackground.setRenderer === 'function')
          window.__dulukaOscBackground.setRenderer(data.background);
      }
      vm.load=function () {
        if (vm.busy || disposed) return;
        vm.busy=true;vm.error='';
        return query('QUERY_OSC_GET_RENDERERS').then(function (data) {
          if(disposed)return;accept(data);vm.loaded=true;
        },function () { if(!disposed)vm.error='l10n.oscRendererLoadFailed'; })
          .finally(function () {if(!disposed)vm.busy=false;});
      };
      vm.save=function () {
        if (!vm.loaded || vm.busy || disposed) return;
        if(!valid(vm.values)) {vm.values=persisted;vm.error='l10n.oscRendererSaveFailed';return;}
        if (JSON.stringify(vm.values) === JSON.stringify(persisted)) return;
        vm.busy=true;vm.error='';vm.saved=false;
        return query('QUERY_OSC_SET_RENDERERS',vm.values).then(function (data) {
          if(disposed)return;accept(data);vm.saved=true;
        },function () {
          if(disposed)return;vm.values={ui:persisted.ui, desktop:persisted.desktop, background:persisted.background};
          vm.error='l10n.oscRendererSaveFailed';
        }).finally(function () {if(!disposed)vm.busy=false;});
      };
      vm.back=function(){return $state.go('main.main-menu');};
      vm.load();
    }]);
})(window);
