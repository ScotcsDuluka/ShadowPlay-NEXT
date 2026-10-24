/* ShadowPlay NEXT Appearance Module Profiles — CEF 73-compatible.
 * Declarative visual assets only. Never evaluates module scripts or code.
 * User-selected ID persisted in a port-independent localhost cookie. */
(function (window, document) {
  'use strict';
  var COOKIE = 'spnext_module_appearance_v1_';
  var moduleIds = ['Container','Overlay','NodeApi','Capture','Graphics','Helper',
                   'Adapter','Hook','Ansel','Ffmpeg','Config'];
  function targetModule() {
    var id = window.__quickModuleTarget || 'Container';
    return moduleIds.indexOf(id) >= 0 ? id : 'Container';
  }
  var fallback = { id: 'default', name: 'NVIDIA Classic',
    version: '1.0.0', author: 'ShadowPlay NEXT', background: '',
    accent: '#76b900', accentBright: '#9bd532' };
  var profiles = [fallback];
  var picker = document.getElementById('appearanceProfile');
  var apply = document.getElementById('btnApplyAppearance');
  var info = document.getElementById('appearanceInfo');
  var installButton = document.getElementById('btnInstallAppearance');
  var importButton = document.getElementById('btnImportBackground');
  var removeButton = document.getElementById('btnRemoveAppearance');
  var preview = document.getElementById('appearancePreview');
  var previewName = document.getElementById('appearancePreviewName');
  var installedCount = document.getElementById('appearanceInstalledCount');
  var working = false;
  function message(en, th) {
    var language = window.LauncherI18n && window.LauncherI18n.getLanguage
      ? window.LauncherI18n.getLanguage() : 'en-US';
    return language === 'th-TH' ? th : en;
  }
  function busy(state) {
    working = state;
    if (installButton) installButton.disabled = state;
    if (importButton) importButton.disabled = state;
    if (apply) apply.disabled = state;
    if (picker) picker.disabled = state;
    if (removeButton) removeButton.disabled = state || picker.value === 'default';
  }
  function feedback(text) {
    if (info) info.textContent = text;
  }
  if (!picker || !apply || !document.createElement) return;

  function clean(obj) {
    if (!obj || typeof obj !== 'object') return null;
    var id = obj.id, background = obj.background || '';
    if (typeof id !== 'string' || !/^[a-z][a-z0-9-]{0,39}$/.test(id)) return null;
    if (typeof background !== 'string' ||
        (background && !new RegExp('^profiles/assets/' + id + '/[a-z0-9_-]{1,40}\\.(png|jpg|jpeg|webp)$').test(background))) return null;
    var accent = obj.accent || '#76b900';
    var bright = obj.accentBright || accent;
    if (!/^#[a-fA-F0-9]{6}$/.test(accent) || !/^#[a-fA-F0-9]{6}$/.test(bright)) return null;
    if (typeof obj.name !== 'string' || !obj.name || obj.name.length > 70) return null;
    return {
      id: id, name: obj.name, version: String(obj.version || '1.0.0').slice(0, 24),
      author: String(obj.author || 'Unknown').slice(0, 60),
      background: background, accent: accent, accentBright: bright
    };
  }
  function savedId() {
    var parts = document.cookie ? document.cookie.split(';') : [];
    for (var i = 0; i < parts.length; i++) {
      var kv = parts[i].trim().split('=');
      if (kv[0] === COOKIE + targetModule()) {
        var id = kv[1] || '';
        return /^[a-z][a-z0-9-]{0,39}$/.test(id) ? id : 'default';
      }
    }
    return 'default';
  }
  function storeId(id) {
    document.cookie = COOKIE + targetModule() + '=' + id + '; Max-Age=31536000; Path=/; SameSite=Lax';
  }
  function find(id) {
    for (var i = 0; i < profiles.length; i++)
      if (profiles[i].id === id) return profiles[i];
    return fallback;
  }
  function previewProfile(id) {
    var chosen = find(id);
    if (preview && preview.style) {
      preview.style.backgroundImage = chosen.background
        ? 'linear-gradient(rgba(8,10,8,.14),rgba(8,10,8,.25)),url("' + chosen.background + '")'
        : 'linear-gradient(140deg,#121c14,#293923)';
      preview.style.backgroundSize = 'cover';
      preview.style.backgroundPosition = 'center';
    }
    if (previewName) previewName.textContent = chosen.name;
  }
  function selectProfile(id, save) {
    var profile = find(id);
    // Module-scoped visual assets only. Never modify Launcher main/HTML
    // accent or background: the module being configured is the owner.
    var moduleId = targetModule();
    var card = document.querySelector &&
      document.querySelector('.quick-module[data-module="' + moduleId + '"]');
    var visual = profile.background
      ? 'linear-gradient(rgba(8,10,8,.67),rgba(8,10,8,.83)),url("' + profile.background + '")'
      : 'linear-gradient(105deg,#1a221b,#141914)';
    if (card && card.style) {
      card.style.backgroundImage = visual;
      card.style.backgroundSize = 'cover';
      card.style.backgroundPosition = 'center';
      card.style.borderLeftColor = profile.accent;
    }
    var panel = document.getElementById('quickConfigPreview');
    if (panel && panel.style) {
      panel.style.backgroundImage = visual;
      panel.style.backgroundSize = 'cover';
      panel.style.backgroundPosition = 'center';
      panel.style.borderLeftColor = profile.accent;
    }
    picker.value = profile.id;
    previewProfile(profile.id);
    if (removeButton) removeButton.disabled = working || profile.id === 'default';
    if (info) info.textContent = profile.name + ' · v' + profile.version + ' · ' + profile.author;
    if (save) storeId(profile.id);
  }
  function redraw() {
    while (picker.firstChild) picker.removeChild(picker.firstChild);
    for (var i = 0; i < profiles.length; i++) {
      var option = document.createElement('option');
      option.value = profiles[i].id;
      option.textContent = profiles[i].name;
      picker.appendChild(option);
    }
    // Restore per-module backgrounds on all existing core module cards.
    var original = window.__quickModuleTarget || 'Container';
    for (var mi = 0; mi < moduleIds.length; mi++) {
      window.__quickModuleTarget = moduleIds[mi];
      selectProfile(savedId(), false);
    }
    window.__quickModuleTarget = original;
    selectProfile(savedId(), false);
    if (installedCount) {
      var num = profiles.length - 1;
      installedCount.textContent = message(
        num + ' appearance module' + (num === 1 ? '' : 's') + ' installed',
        'ติดตั้งโมดูลตกแต่งแล้ว ' + num + ' รายการ');
    }
  }
  apply.addEventListener('click', function () {
    selectProfile(picker.value, true);
  });
  window.__onModuleAppearanceTargetChanged = function () {
    selectProfile(savedId(), false);
  };
  // Browser still looks correct if the optional registry is missing.
  redraw();
  if (typeof XMLHttpRequest !== 'function') return;
  function loadCatalog(afterLoad) {
    var req = new XMLHttpRequest();
    req.open('GET', 'profiles/catalog.json?v=' + Date.now(), true);
    req.onreadystatechange = function () {
      if (req.readyState !== 4) return;
      var next = [fallback];
      if (req.status === 200) {
        try {
          var payload = JSON.parse(req.responseText);
          if (payload && payload.schemaVersion === 1 &&
              Array.isArray(payload.profiles) && payload.profiles.length <= 31) {
            var seen = { 'default': true };
            for (var i = 0; i < payload.profiles.length; i++) {
              var item = clean(payload.profiles[i]);
              if (!item || seen[item.id]) continue;
              seen[item.id] = true;
              next.push(item);
            }
          }
        } catch (err) {
          if (window.console && window.console.warn)
            window.console.warn('[appearance] invalid catalog ignored', err);
        }
      }
      profiles = next;
      // Removed/unavailable profiles must never silently re-activate later.
      if (find(savedId()).id !== savedId()) storeId('default');
      redraw();
      if (afterLoad) afterLoad();
    };
    req.send(null);
  }

  function moduleRpc(command, id) {
    return new Promise(function (resolve, reject) {
      if (typeof window.cefQuery !== 'function') {
        reject(new Error('Native Launcher is not connected'));
        return;
      }
      var settled = false;
      var timeout = setTimeout(function () {
        if (settled) return;
        settled = true;
        reject(new Error('Module operation timed out'));
      }, 110000);
      var request = { command: command };
      if (id) request.id = id;
      window.cefQuery({
        request: JSON.stringify(request),
        persistent: false,
        onSuccess: function (response) {
          if (settled) return;
          settled = true;
          clearTimeout(timeout);
          try { resolve(JSON.parse(response)); }
          catch (e) { reject(new Error('Invalid native response')); }
        },
        onFailure: function (code, msg) {
          if (settled) return;
          settled = true;
          clearTimeout(timeout);
          reject(new Error(String(msg || code)));
        }
      });
    });
  }

  picker.addEventListener('change', function () {
    previewProfile(picker.value);
    if (removeButton) removeButton.disabled = working || picker.value === 'default';
  });
  if (importButton) importButton.addEventListener('click', function () {
    if (working) return;
    busy(true);
    feedback(message('Choose a PNG/JPEG/WEBP image…', 'เลือกรูปพื้นหลัง PNG/JPEG/WEBP…'));
    moduleRpc('LAUNCHER_IMPORT_BACKGROUND').then(function (result) {
      if (result && result.cancelled) {
        feedback(message('Import cancelled', 'ยกเลิกการนำเข้า'));
      } else if (result && result.ok) {
        loadCatalog(function () {
          feedback(message('Background imported as a module. Select it and apply.',
                           'นำเข้ารูปเป็นโมดูลแล้ว เลือกโปรไฟล์เพื่อใช้งาน'));
        });
      } else {
        throw new Error('Unable to import or validate background image');
      }
    }).catch(function (error) {
      feedback(message('Import failed: ', 'นำเข้าไม่สำเร็จ: ') + error.message);
    }).then(function () { busy(false); });
  });
  if (installButton) installButton.addEventListener('click', function () {
    if (working) return;
    busy(true);
    feedback(message('Select a .spmod appearance package…',
                     'เลือกแพ็กตกแต่ง .spmod…'));
    moduleRpc('LAUNCHER_INSTALL_APPEARANCE').then(function (result) {
      if (result && result.cancelled) {
        feedback(message('Installation cancelled', 'ยกเลิกการติดตั้งแล้ว'));
      } else if (result && result.ok) {
        loadCatalog(function () {
          feedback(message('Profile installed. Select and apply it.',
                           'ติดตั้งแล้ว เลือกโปรไฟล์แล้วกดใช้โปรไฟล์'));
        });
      } else {
        throw new Error('Unable to install or validate the package');
      }
    }).catch(function (error) {
      feedback(message('Install failed: ', 'ติดตั้งไม่สำเร็จ: ') + error.message);
    }).then(function () { busy(false); });
  });
  if (removeButton) removeButton.addEventListener('click', function () {
    var id = picker.value;
    if (working || id === 'default' ||
        !/^[a-z][a-z0-9-]{0,39}$/.test(id)) return;
    if (!window.confirm || !window.confirm(
        message('Remove this appearance module? A backup will be saved.',
                'ถอนโมดูลตกแต่งนี้หรือไม่? ระบบจะสำรองข้อมูลก่อน'))) return;
    busy(true);
    feedback(message('Removing module…', 'กำลังถอนโมดูล…'));
    moduleRpc('LAUNCHER_UNINSTALL_APPEARANCE', id).then(function (result) {
      if (!result || !result.ok) throw new Error('Native uninstall failed');
      var wasActive = savedId() === id;
      if (wasActive) storeId('default');
      loadCatalog(function () {
        feedback(wasActive
          ? message('Module removed. NVIDIA Classic restored.',
                    'ถอนแล้ว กลับไปใช้ NVIDIA Classic')
          : message('Selected module removed; active profile preserved.',
                    'ถอนโมดูลที่เลือกแล้ว โปรไฟล์ที่ใช้อยู่ยังคงเดิม'));
      });
    }).catch(function (error) {
      feedback(message('Removal failed: ', 'ถอนไม่สำเร็จ: ') + error.message);
    }).then(function () { busy(false); });
  });
  loadCatalog();
})(window, document);
