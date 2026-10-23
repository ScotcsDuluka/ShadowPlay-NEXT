/* NVIDIA ShadowPlay NEXT — V5 layout domain model.
 * Pure data/geometry layer. No DOM, OSC route, backend, or native md-menu changes.
 * Default native-control arrangement is taken from the working NVIDIA OSC menu.
 * Kept separate from experimental Group Layout A (NvGroupLayoutA / Unified V4).
 */
(function (root, factory) {
  'use strict';
  var model = factory();
  if (typeof module === 'object' && module.exports) module.exports = model;
  if (root) root.NvShadowPlayLayoutV5 = model;
})(typeof window !== 'undefined' ? window : null, function () {
  'use strict';

  var PANEL_NAMES = ['left', 'center', 'right'];
  var CONTROLS = ['Screenshot', 'Mods', 'ManualRecord', 'Stream', 'InstantReplay',
    'Ansel', 'Broadcast', 'Performance', 'Mic', 'Camera', 'Gallery', 'Settings'];
  var KNOWN = {};
  CONTROLS.forEach(function (key) { KNOWN[key] = true; });
  var PRESETS = {
    small: { triple: [1, 1, 1], wideLeft: [2, 1], wideRight: [1, 2], single: [3] },
    side: { single: [3], detailLeft: [2, 1], detailRight: [1, 2] },
    big: { single: [3] }
  };
  var MAX_GROUPS = 6, GROUP_SIZE = 240, ROW_SIZE = 80;

  function copy(data) { return JSON.parse(JSON.stringify(data)); }
  function ensure(condition, message) { if (!condition) throw Error(message); }
  function count(s) {
    return PANEL_NAMES.reduce(function (n, p) { return n + s.panels[p].length; }, 0);
  }
  function blank() {
    return { version: 5, autoFill: true,
      panels: { left: [], center: [], right: [] } };
  }
  function block(kind, preset, assigned) {
    ensure(PRESETS[kind] && PRESETS[kind][preset], 'Unknown Block Type / Layout');
    var count = PRESETS[kind][preset].length;
    ensure(!assigned || assigned.length <= count, 'Too many Controls for Slots');
    return { type: kind, layout: preset,
      slots: Array.from({ length: count }, function (_, i) {
        return assigned && assigned[i] || null;
      }) };
  }
  function group(id, blocks) {
    ensure(typeof id === 'string' && /^g[1-9][0-9]*$/.test(id), 'Invalid Group ID');
    return { id: id, blocks: blocks || [] };
  }
  function defaultLayout() {
    var s = blank();
    s.panels.left.push(group('g1', [
      block('side', 'single', ['Screenshot']),
      block('side', 'single', ['Mods']),
      block('side', 'single', ['ManualRecord'])
    ]));
    s.panels.center.push(group('g2', [block('big', 'single', ['Stream'])]));
    s.panels.center.push(group('g3', [block('big', 'single', ['InstantReplay'])]));
    s.panels.center.push(group('g4', [block('big', 'single', ['Ansel'])]));
    s.panels.center.push(group('g5', [block('big', 'single', ['Broadcast'])]));
    s.panels.right.push(group('g6', [
      block('small', 'triple', ['Performance', 'Mic', 'Camera']),
      block('side', 'single', ['Gallery']),
      block('side', 'single', ['Settings'])
    ]));
    validate(s);
    return s;
  }
  function hasControl(g, key) {
    return g.blocks.some(function (b) { return b.slots.indexOf(key) >= 0; });
  }
  function filled(g) {
    // Empty GROUP means no Blocks, not merely unassigned Controls in its Slots.
    return g.blocks.length > 0;
  }
  function validate(s) {
    ensure(s && s.version === 5 && typeof s.autoFill === 'boolean' &&
      s.panels && typeof s.panels === 'object', 'Expected Layout V5 state');
    var ids = {}, keys = {}, groups = 0;
    PANEL_NAMES.forEach(function (p) {
      ensure(Array.isArray(s.panels[p]), 'Missing Panel ' + p);
      s.panels[p].forEach(function (g) {
        groups++;
        ensure(g && typeof g.id === 'string' && /^g[1-9][0-9]*$/.test(g.id) &&
          !ids[g.id], 'Duplicate or missing Group ID');
        ids[g.id] = true;
        ensure(Array.isArray(g.blocks) && g.blocks.length <= 3, 'Group exceeds 3 Blocks');
        var rows = 0;
        g.blocks.forEach(function (b) {
          ensure(b && PRESETS[b.type] && PRESETS[b.type][b.layout],
            'Unknown Block Type / Layout');
          ensure(Array.isArray(b.slots) && b.slots.length === PRESETS[b.type][b.layout].length,
            'Invalid Slot count');
          rows += b.type === 'big' ? 3 : 1;
          b.slots.forEach(function (key) {
            if (key === null) return;
            ensure(typeof key === 'string' && KNOWN[key], 'Unknown Control ' + key);
            ensure(!keys[key], 'Duplicate Control ' + key);
            keys[key] = true;
          });
        });
        ensure(rows <= 3, 'Group height exceeds 240px');
        ensure(!g.blocks.some(function (b) { return b.type === 'big'; }) ||
          g.blocks.length === 1, 'Big requires the whole Group');
      });
    });
    ensure(groups <= MAX_GROUPS, 'Only six Groups may exist across all Panels');
    ensure(keys.Settings === true, 'Settings cannot be removed');
    return true;
  }
  function locate(s, id) {
    for (var p = 0; p < PANEL_NAMES.length; p++) {
      var panel = PANEL_NAMES[p];
      for (var i = 0; i < s.panels[panel].length; i++) {
        if (s.panels[panel][i].id === id)
          return { panel: panel, index: i, group: s.panels[panel][i] };
      }
    }
    return null;
  }
  function findControl(s, key) {
    for (var p = 0; p < PANEL_NAMES.length; p++) {
      var panel = PANEL_NAMES[p], list = s.panels[panel];
      for (var gi = 0; gi < list.length; gi++) {
        for (var bi = 0; bi < list[gi].blocks.length; bi++) {
          var si = list[gi].blocks[bi].slots.indexOf(key);
          if (si !== -1) return {
            panel: panel, groupId: list[gi].id, block: bi, slot: si
          };
        }
      }
    }
    return null;
  }
  function available(s) {
    validate(s);
    return CONTROLS.filter(function (key) { return !findControl(s, key); });
  }
  function change(s, fn) {
    validate(s);
    var next = copy(s);
    fn(next);
    validate(next);
    return next;
  }
  function newId(s) {
    var next = 1;
    while (locate(s, 'g' + next)) next++;
    return 'g' + next;
  }
  function checkPanel(p) {
    ensure(PANEL_NAMES.indexOf(p) !== -1, 'Unknown Panel ' + p);
  }
  function insertAt(list, item, index) {
    var position = index === undefined ? list.length : index;
    ensure(Number.isInteger(position) && position >= 0 && position <= list.length,
      'Invalid insertion index');
    list.splice(position, 0, item);
  }
  function addGroup(s, panel, index) {
    checkPanel(panel);
    return change(s, function (n) {
      ensure(count(n) < MAX_GROUPS, 'Only six Groups may exist across all Panels');
      insertAt(n.panels[panel], group(newId(n)), index);
    });
  }
  function deleteGroup(s, id) {
    return change(s, function (n) {
      var target = locate(n, id);
      ensure(target, 'Unknown Group');
      ensure(!hasControl(target.group, 'Settings'),
        'Move Settings before deleting its Group');
      // All other Controls are implicitly returned to the available catalog.
      n.panels[target.panel].splice(target.index, 1);
    });
  }
  function removeBlock(s, id, blockIndex) {
    return change(s, function (n) {
      var loc = locate(n, id);
      ensure(loc && loc.group.blocks[blockIndex], 'Missing Block');
      ensure(!loc.group.blocks[blockIndex].slots.includes('Settings'),
        'Move Settings before removing its Block');
      loc.group.blocks.splice(blockIndex, 1);
    });
  }
  function addBlock(s, id, type, layout, index, replaceBig) {
    return change(s, function (n) {
      var loc = locate(n, id);
      ensure(loc, 'Missing Group');
      var b = block(type, layout), g = loc.group;
      if (type === 'big' && g.blocks.length) {
        ensure(replaceBig === true, 'Big needs empty Group or explicit replacement');
        ensure(!hasControl(g, 'Settings'), 'Move Settings before replacing Group with Big');
        g.blocks = [b];
      } else {
        ensure(!g.blocks.some(function (x) { return x.type === 'big'; }),
          'Cannot add Block alongside Big');
        ensure(g.blocks.length < 3, 'Only three Small/Side Blocks per Group');
        insertAt(g.blocks, b, index);
      }
    });
  }
  function changeBlock(s, id, blockIndex, type, layout) {
    return change(s, function (n) {
      var loc = locate(n, id);
      ensure(loc && loc.group.blocks[blockIndex], 'Missing Block');
      var g = loc.group, original = g.blocks[blockIndex], next = block(type, layout);
      var assigned = original.slots.filter(Boolean);
      var kept = assigned.slice(0, next.slots.length);
      var discarded = assigned.slice(next.slots.length);
      ensure(discarded.indexOf('Settings') < 0,
        'Move Settings before reducing Slots');
      if (type === 'big' && g.blocks.length > 1) {
        ensure(!g.blocks.some(function (b, i) {
          return i !== blockIndex && b.slots.includes('Settings');
        }), 'Move Settings before replacing other Blocks with Big');
        g.blocks = [next];
      } else {
        g.blocks[blockIndex] = next;
      }
      kept.forEach(function (key, i) { next.slots[i] = key; });
      // Discarded non-Settings Controls are now available again.
    });
  }
  function targetSlot(n, id, bi, si) {
    var loc = locate(n, id);
    ensure(loc && Number.isInteger(bi) && loc.group.blocks[bi], 'Missing Block');
    var b = loc.group.blocks[bi];
    ensure(Number.isInteger(si) && si >= 0 && si < b.slots.length, 'Missing Slot');
    return b.slots;
  }
  function placeControl(s, key, groupId, bi, si) {
    ensure(KNOWN[key], 'Unknown Control ' + key);
    return change(s, function (n) {
      var slots = targetSlot(n, groupId, bi, si);
      var from = findControl(n, key), replaced = slots[si];
      if (replaced === key) return;
      if (replaced && !from)
        throw Error('Move a Control into an empty Slot before swapping');
      if (from) targetSlot(n, from.groupId, from.block, from.slot)[from.slot] =
        replaced || null;
      slots[si] = key;
    });
  }
  function takeControl(s, key) {
    ensure(key !== 'Settings', 'Settings cannot be removed');
    return change(s, function (n) {
      var from = findControl(n, key);
      ensure(from, 'Control not assigned');
      targetSlot(n, from.groupId, from.block, from.slot)[from.slot] = null;
    });
  }
  function moveGroup(s, id, destPanel, index) {
    checkPanel(destPanel);
    return change(s, function (n) {
      var from = locate(n, id);
      ensure(from, 'Unknown Group');
      var before = n.panels[destPanel].length;
      var to = index === undefined ? before : index;
      ensure(Number.isInteger(to) && to >= 0 && to <= before, 'Invalid insertion index');
      var moved = n.panels[from.panel].splice(from.index, 1)[0];
      if (destPanel === from.panel && to > from.index) to--;
      n.panels[destPanel].splice(to, 0, moved);
    });
  }
  function swapGroups(s, idA, idB) {
    return change(s, function (n) {
      var a = locate(n, idA), b = locate(n, idB);
      ensure(a && b, 'Unknown Group to swap');
      n.panels[a.panel][a.index] = b.group;
      n.panels[b.panel][b.index] = a.group;
    });
  }
  function autoFill(s, value) {
    return change(s, function (n) { n.autoFill = !!value; });
  }
  function geometry(s, mode, width) {
    validate(s);
    ensure(mode === 'normal' || mode === 'edit', 'Invalid Mode');
    var showEmpty = mode === 'edit', gap = 28, x = 0, boxes = [], cells = [], slots = [];
    PANEL_NAMES.forEach(function (p) {
      var groups = s.panels[p].filter(function (g) { return showEmpty || filled(g); });
      groups.forEach(function (g) {
        var dy = 0, height = GROUP_SIZE;
        boxes.push({ id: g.id, panel: p, x: x, y: 0,
          width: GROUP_SIZE, height: height, empty: !filled(g) });
        g.blocks.forEach(function (b, bi) {
          var h = b.type === 'big' ? GROUP_SIZE :
            s.autoFill ? GROUP_SIZE / g.blocks.length : ROW_SIZE;
          var spans = PRESETS[b.type][b.layout], total = spans.reduce(function (n, v) { return n + v; }, 0), sx = 0;
          b.slots.forEach(function (key, si) {
            var w = GROUP_SIZE * spans[si] / total;
            slots.push({ control: key, panel: p, groupId: g.id,
              block: bi, slot: si, type: b.type, layout: b.layout,
              x: x + sx, y: dy, width: w, height: h });
            if (key) cells.push({
              control: key, panel: p, groupId: g.id,
              block: bi, slot: si, type: b.type,
              x: x + sx, y: dy, width: w, height: h,
              // CSS should center native md-icon; never stretch icon to cell.
              iconScale: 1
            });
            sx += w;
          });
          dy += h;
        });
        x += GROUP_SIZE;
      });
      if (groups.length) x += gap;
    });
    if (x) x -= gap;
    var targetWidth = Number(width) || 1920;
    var scale = Math.min(1, Math.max(.01, targetWidth / Math.max(1, x)));
    return { width: x, height: GROUP_SIZE, scale: scale,
      boxes: boxes, cells: cells, slots: slots,
      hiddenGroupIds: PANEL_NAMES.reduce(function (a, p) {
        s.panels[p].forEach(function (g) {
          if (!showEmpty && !filled(g)) a.push(g.id);
        }); return a;
      }, []) };
  }
  // A drag insertion preview is a pure proposal; the caller supplies the
  // hover delay and only commits the returned layout after a real drop.
  function insertionPreview(s, id, panel, index) {
    var draft = moveGroup(s, id, panel, index);
    return { pending: true, groupId: id, panel: panel, index: index,
      layout: draft, geometry: geometry(draft, 'edit', 1920) };
  }
  function createEditSession(committed, persist) {
    validate(committed);
    var original = copy(committed), draft = copy(committed), history = [], editing = true;
    return {
      isEditing: function () { return editing; },
      get: function () { return copy(draft); },
      apply: function (edit) {
        ensure(editing && typeof edit === 'function', 'No Edit session');
        var next = edit(copy(draft)); validate(next);
        history.push(copy(draft)); draft = copy(next);
        return copy(draft);
      },
      undo: function () {
        ensure(editing, 'No Edit session');
        if (history.length) draft = history.pop();
        return copy(draft);
      },
      done: function () {
        ensure(editing, 'No Edit session');
        validate(draft);
        if (typeof persist === 'function') persist(copy(draft));
        original = copy(draft); history = []; editing = false;
        return copy(original);
      },
      cancel: function () {
        ensure(editing, 'No Edit session');
        draft = copy(original); history = []; editing = false;
        return copy(draft);
      }
    };
  }
  return {
    VERSION: 5, MAX_GROUPS: MAX_GROUPS, GROUP_SIZE: GROUP_SIZE,
    PANEL_NAMES: PANEL_NAMES, CONTROLS: CONTROLS, PRESETS: PRESETS,
    blank: blank, defaultLayout: defaultLayout, validate: validate, clone: copy,
    count: count, locate: locate, findControl: findControl, available: available,
    addGroup: addGroup, deleteGroup: deleteGroup, addBlock: addBlock,
    removeBlock: removeBlock, changeBlock: changeBlock, placeControl: placeControl,
    takeControl: takeControl, moveGroup: moveGroup, swapGroups: swapGroups,
    autoFill: autoFill, geometry: geometry, insertionPreview: insertionPreview,
    createEditSession: createEditSession
  };
});
