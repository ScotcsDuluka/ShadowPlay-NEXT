(function (root, factory) {
  'use strict';
  if (typeof module === 'object' && module.exports) module.exports = factory;
  else if (root.angular) {
    var injector = root.angular.element(root.document).injector();
    if (injector) factory(injector);
    else root.angular.module('main').run(['$injector', factory]);
  }
})(typeof window === 'undefined' ? this : window, function installLiveHotkeys(injector) {
  'use strict';
  var service = injector.get('hotkeyService');
  if (service.__dulukaLiveHotkeys) return;
  service.__dulukaLiveHotkeys = true;
  var shadow = injector.get('shadowPlayService'), q = injector.get('$q');
  var active = new Set(), revisions = Object.create(null), confirmed = Object.create(null), saves = Object.create(null);
  var getShortcut = shadow.getHotkeyShortcut, setShortcut = shadow.setHotkeyShortcut;
  function key(name) { return String(name).toLowerCase(); }
  function replace(binding, keys) {
    binding.hotKey = keys.slice();
    binding.hotKeyCount = keys.length;
    binding.keyMatchCount = 0;
    binding.invalidSequence = false;
  }
  shadow.getHotkeyShortcut = function (name) {
    var id = key(name), version = revisions[id] || 0;
    return getShortcut.apply(this, arguments).then(function (reply) {
      // A constructor's old read must not overwrite a just-saved binding.
      if (version !== (revisions[id] || 0) && confirmed[id])
        return Object.assign({}, reply, {keys:confirmed[id].slice()});
      return reply;
    });
  };
  shadow.setHotkeyShortcut = function (name, keys) {
    var id = key(name), copy = keys.slice(), context = this;
    // Preserve write order for the same action, including rapid successive edits.
    var previous = saves[id] || q.when();
    var result = previous.catch(function () {}).then(function () {
      return setShortcut.call(context, name, copy);
    }).then(function (value) {
      confirmed[id] = copy.slice();
      revisions[id] = (revisions[id] || 0) + 1;
      active.forEach(function (binding) {
        if (key(binding.hotKeyName) === id) replace(binding, copy);
      });
      return value;
    });
    var tail = result.finally(function () { if (saves[id] === tail) delete saves[id]; });
    saves[id] = tail;
    return tail;
  };
  var create = service.createHotKey;
  service.createHotKey = function () {
    var binding = create.apply(this, arguments);
    var start = binding.startHotkeyDetection, stop = binding.stopHotKeyDetection;
    binding.startHotkeyDetection = function () {
      var args = arguments, context = this;
      // INSERT and other local UI controls are not configurable ShadowPlay keys.
      if (service.nonSPHK && Object.prototype.hasOwnProperty.call(service.nonSPHK, binding.hotKeyName))
        return start.apply(context, args);
      active.add(binding);
      // A stopped controller can be reused after its shortcut was edited.
      return shadow.getHotkeyShortcut(binding.hotKeyName).then(function (reply) {
        if (!active.has(binding)) return;
        if (reply && Array.isArray(reply.keys)) replace(binding, reply.keys);
        return start.apply(context, args);
      }).catch(function (error) { active.delete(binding); throw error; });
    };
    binding.stopHotKeyDetection = function () {
      active.delete(binding);
      return stop.apply(this, arguments);
    };
    return binding;
  };
});
