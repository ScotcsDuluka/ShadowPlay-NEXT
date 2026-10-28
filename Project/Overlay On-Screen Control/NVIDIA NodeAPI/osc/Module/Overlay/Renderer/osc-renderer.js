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
    '<div ng-if="rendererSettings.anyGpu()" class="osc-gpu-adapter-preference">',
    '<md-input-container><label translate="l10n.oscRendererAdapterLabel">Preferred GPU Adapter (Phase 0)</label>',
    '<md-select class="osc-select resolution-fps-select" ng-model="rendererSettings.gpuAdapter" ng-change="rendererSettings.save()" ng-disabled="!rendererSettings.loaded || rendererSettings.busy || !rendererSettings.gpuAdapterSupported" aria-label="Preferred GPU Adapter" hover-focus>',
    '<md-option md-ink-ripple="false" ng-repeat="adapter in rendererSettings.gpuAdapters track by adapter.id" ng-value="adapter.id" hover-focus><span ng-if="adapter.id === rendererSettings.autoAdapter" translate="l10n.oscRendererAuto">Auto</span><span ng-if="adapter.id !== rendererSettings.autoAdapter">{{adapter.name}}</span></md-option>',
    '</md-select></md-input-container>',
    '<p class="preferences-instructional-text" translate="l10n.oscRendererAdapterPhase0">The GPU choice is saved as a Phase 0 preference. The Host and CEF still use Windows GPU selection until cross-GPU transfer is validated.</p>',
    '<p class="preferences-instructional-text" ng-if="rendererSettings.activeGpuDisplay"><span translate="l10n.oscRendererActiveGpu">Active Host compositor GPU:</span> {{rendererSettings.activeGpuDisplay}}</p>',
    '<p class="preferences-instructional-text" ng-if="rendererSettings.cefFrameTransport === \'gpu-shared-opened\'" translate="l10n.oscRendererCefShared">CEF shared GPU frame opened successfully on Host.</p>',
    '<p class="preferences-instructional-text" ng-if="rendererSettings.cefFrameTransport === \'cpu-paint\'" translate="l10n.oscRendererCefCpu">CEF is providing CPU paint frames.</p>',
    '<p class="preferences-instructional-text" ng-if="rendererSettings.cefFrameTransport === \'idle\'" translate="l10n.oscRendererCefIdle">No recent CEF frame to verify GPU compatibility.</p>',
    '<p class="preferences-instructional-text" ng-if="!rendererSettings.gpuAdapterAvailable" translate="l10n.oscRendererAdapterMissing">The saved GPU is unavailable. Select Auto or another adapter.</p>',
    '</div>',
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
      vm.gpuAdapter = 'auto';
      vm.autoAdapter = 'auto';
      vm.gpuAdapters = [{id:'auto',name:'Auto (Windows default)'}];
      vm.gpuAdapterSupported = false;
      vm.gpuAdapterAvailable = true;
      vm.activeGpuDisplay = '';
      vm.cefFrameTransport = '';
      vm.anyGpu = function () {return vm.values.ui === 'gpu' || vm.values.desktop === 'gpu';};
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
            request.gpuAdapter=values.gpuAdapter;
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
        var gpuAdapter = typeof data.gpuAdapter === 'string' ? data.gpuAdapter : 'auto';
        persisted={ui:data.ui, desktop:data.desktop, background:data.background, gpuAdapter:gpuAdapter};
        vm.values={ui:data.ui, desktop:data.desktop, background:data.background};
        vm.gpuAdapters=Array.isArray(data.gpuAdapters) ? data.gpuAdapters.filter(function (item) {
          return item && typeof item.id === 'string' && typeof item.name === 'string';
        }) : [{id:'auto',name:'Auto (Windows default)'}];
        vm.gpuAdapterSupported=Array.isArray(data.gpuAdapters) && vm.gpuAdapters.length > 0;
        vm.gpuAdapterAvailable=data.gpuAdapterAvailable !== false;
        vm.gpuAdapter=gpuAdapter;
        var activeGpuId = typeof data.activeGpuAdapter === 'string' ? data.activeGpuAdapter : '';
        var activeGpuName = typeof data.activeGpuAdapterName === 'string' ? data.activeGpuAdapterName : '';
        vm.activeGpuDisplay = activeGpuName ? activeGpuName + (activeGpuId ? ' (' + activeGpuId + ')' : '') : activeGpuId;
        vm.cefFrameTransport = typeof data.cefFrameTransport === 'string' ? data.cefFrameTransport : '';
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
        if(!valid(vm.values) || (vm.gpuAdapterSupported &&
            !vm.gpuAdapters.some(function (item) {return item.id === vm.gpuAdapter;}))) {
          vm.values={ui:persisted.ui, desktop:persisted.desktop, background:persisted.background};
          vm.gpuAdapter=persisted.gpuAdapter;
          vm.error='l10n.oscRendererSaveFailed';return;
        }
        var next={ui:vm.values.ui, desktop:vm.values.desktop,
          background:vm.values.background, gpuAdapter:vm.gpuAdapter};
        if (JSON.stringify(next) === JSON.stringify(persisted)) return;
        vm.busy=true;vm.error='';vm.saved=false;
        return query('QUERY_OSC_SET_RENDERERS',next).then(function (data) {
          if(disposed)return;accept(data);vm.saved=true;
        },function () {
          if(disposed)return;vm.values={ui:persisted.ui, desktop:persisted.desktop, background:persisted.background};
          vm.gpuAdapter=persisted.gpuAdapter;
          vm.error='l10n.oscRendererSaveFailed';
        }).finally(function () {if(!disposed)vm.busy=false;});
      };
      vm.back=function(){return $state.go('main.main-menu');};
      vm.load();
    }]);
})(window);
