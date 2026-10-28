/* OSC mod UI localization bridge for CEF 73.
 * The native Angular Translate service is the single language authority.
 * Mod-owned DOM text, dynamic statuses, accessible names and tooltips often
 * bypass Angular templates. Bind those known phrases without touching SVG,
 * NVIDIA icon glyphs, game paths, user text, or backend process state.
 */
(function (window, document) {
  'use strict';
  if (!window.angular || !window.__oscUiPhraseKeys) return;
  var phraseKeys = window.__oscUiPhraseKeys;
  var attributes = ['title', 'aria-label', 'placeholder'];
  // Some late-created NVIDIA nodes carry translate="l10n.*" but are never
  // recompiled by Angular. Translate their English fallback text as well.
  var ignored = 'md-icon, .share-icon, .nv-flex-icon, .osc-tile-icon, svg, path, use, i, script, style, code, pre, [contenteditable], [data-osc-no-translation]';
  var records = new WeakMap();
  var activeLanguage = '';
  var convert = function (key, fallback) { return fallback; };
  var observer = null;
  var pending = [];
  var scheduled = false;
  var running = false;

  function iconOrManaged(node) {
    var parent = node.nodeType === 3 ? node.parentElement : node;
    return !parent || (parent.closest && !!parent.closest(ignored));
  }

  function translated(original) {
    var key = phraseKeys[original];
    if (!key || activeLanguage === 'en-US') return original;
    var value = convert(key, original);
    return typeof value === 'string' && value && value !== key ? value : original;
  }

  function localizeText(node) {
    if (!node || node.nodeType !== 3 || iconOrManaged(node)) return;
    var actual = node.nodeValue || '';
    if (!actual || actual.length > 350) return;
    var record = records.get(node);
    var space = actual.match(/^(\s*)([\s\S]*?)(\s*)$/);
    var base = space ? space[2] : actual;
    if (record && record.rendered === actual) {
      var changed = translated(record.source);
      var desired = record.prefix + changed + record.suffix;
      if (desired !== actual) {
        record.rendered = desired;
        node.nodeValue = desired;
      }
      return;
    }
    if (!Object.prototype.hasOwnProperty.call(phraseKeys, base)) {
      if (record) records.delete(node);
      return;
    }
    record = {source: base, prefix: space[1], suffix: space[3], rendered: actual};
    records.set(node, record);
    var next = record.prefix + translated(record.source) + record.suffix;
    if (next !== actual) {
      record.rendered = next;
      node.nodeValue = next;
    }
  }

  function localizeAttribute(element, name) {
    if (!element || element.nodeType !== 1 || iconOrManaged(element)) return;
    if (!element.hasAttribute(name)) return;
    var actual = element.getAttribute(name);
    if (!actual || actual.length > 350) return;
    var map = records.get(element) || {};
    var rec = map[name];
    if (rec && actual === rec.rendered) {
      var changed = translated(rec.source);
      if (changed !== actual) {
        rec.rendered = changed;
        element.setAttribute(name, changed);
      }
      return;
    }
    if (!Object.prototype.hasOwnProperty.call(phraseKeys, actual)) {
      if (rec) { delete map[name]; records.set(element, map); }
      return;
    }
    rec = { source: actual, rendered: actual };
    map[name] = rec;
    records.set(element, map);
    var next = translated(actual);
    if (actual !== next) {
      rec.rendered = next;
      element.setAttribute(name, next);
    }
  }

  function visit(node) {
    if (!node) return;
    if (node.nodeType === 3) { localizeText(node); return; }
    if (node.nodeType !== 1 || iconOrManaged(node)) return;
    for (var i = 0; i < attributes.length; i++) localizeAttribute(node, attributes[i]);
    // TreeWalker is cheaper than querySelectorAll followed by many text
    // lookups, and leaves Angular's DOM structure and event handlers intact.
    var walker = document.createTreeWalker(node, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT, null, false);
    var next;
    while ((next = walker.nextNode())) {
      if (next.nodeType === 3) localizeText(next);
      else if (!iconOrManaged(next))
        for (var j = 0; j < attributes.length; j++) localizeAttribute(next, attributes[j]);
    }
  }

  function flush() {
    scheduled = false;
    if (running) return;
    running = true;
    try {
      var list = pending.splice(0);
      for (var i = 0; i < list.length; i++) visit(list[i]);
    } finally {
      running = false;
    }
  }

  function enqueue(node) {
    if (!node) return;
    pending.push(node);
    if (!scheduled) {
      scheduled = true;
      window.setTimeout(flush, 0);
    }
  }

  function updateLanguage(locale) {
    if (typeof locale !== 'string' || activeLanguage === locale) return;
    activeLanguage = locale;
    // Locale changes are infrequent. A single full pass also updates
    // already-bound labels, including text currently displayed in Thai.
    enqueue(document.body);
  }

  window.angular.module('main').run(['$translate', '$filter', '$rootScope', '$log',
    function ($translate, $filter, $rootScope, $log) {
      var translate = $filter('translate');
      convert = function (key, fallback) {
        try {
          var value = translate(key);
          if (typeof value === 'string' && value.trim() && value !== key)
            return value;
        } catch (error) { $log.warn('[osc-ui-l10n] failed key ' + key, error); }
        return fallback;
      };

      activeLanguage = $translate.use() || 'en-US';
      observer = new MutationObserver(function (mutations) {
        for (var i = 0; i < mutations.length; i++) {
          var change = mutations[i];
          if (change.type === 'childList') {
            for (var j = 0; j < change.addedNodes.length; j++)
              enqueue(change.addedNodes[j]);
          } else if (change.type === 'characterData') enqueue(change.target);
          else if (change.type === 'attributes') enqueue(change.target);
        }
      });
      observer.observe(document.body, {
        childList:true, subtree:true, characterData:true,
        attributes:true, attributeFilter:attributes
      });
      enqueue(document.body);
      $rootScope.$on('$translateChangeSuccess', function (event, data) {
        updateLanguage((data && data.language) || $translate.use());
      });

      window.__oscUiL10n = {
        refresh: function () {
          var locale = $translate.use() || 'en-US';
          if (locale !== activeLanguage) activeLanguage = locale;
          enqueue(document.body);
        },
        current: function () { return activeLanguage; },
        phraseCount: Object.keys(phraseKeys).length,
        keyFor: function (source) { return phraseKeys[source] || ''; }
      };
    }
  ]);
})(window, document);
