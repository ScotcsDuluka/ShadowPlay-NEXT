# ShadowPlay NEXT — Language parity / live OSC audit (2026-10-24)

## Scope
Source: `Project/Overlay On-Screen Control/NVIDIA NodeAPI/osc`.
Launcher source: `Project/Launcher.Cef/ui`.
Branch: `Stable`. Other changes in the worktree are owned by other modules: never reset or bulk-commit them.

## Verified
- 28 OSC locale JSON files, 1,127 keys including `meta.languageName` in the English reference.
- Before this repair, 25 locales were missing 215 keys each; `en-GB` was missing 208. Total **5,583** absent entries.
- Added only absent entries with the *English reference string* as a safe explicit fallback. Original translations were preserved. **These are not 5,583 human translations.**
- `tools/locale-fallbacks.json` records each generated fallback key for translators, including placeholders.
- `node tools/locale-coverage.cjs` audits missing/empty/extra/duplicate keys. `--fix` fills new gaps without overwriting translated entries.
- OSC UI phrase map has 916 mappings and English/Thai parity; all mapped keys exist in the 28 dictionaries.
- Launcher uses an independent JS dictionary: full English/Thai coverage of its current UI; other locales have only a small curated translated subset and intentionally use English fallback for Launcher-specific strings.
- `launcher-language-sync.js`: only NEW Launcher publications override OSC. OSC-internal manual language changes are no longer reset by an unchanged Launcher state every 3 seconds. It still is **Launcher -> OSC**, not a persistent bidirectional preference sync.
- Locale `document.documentElement.lang` now follows successful Angular language changes from OSC itself.

## Asset build / deployment
No native component changed; no C++ rebuild needed for JS/JSON assets.
```powershell
node "Project\Overlay On-Screen Control\NVIDIA NodeAPI\tools\locale-coverage.cjs"
node "Project\Overlay On-Screen Control\NVIDIA NodeAPI\tools\deploy-locales.cjs" --dry-run
node "Project\Overlay On-Screen Control\NVIDIA NodeAPI\tools\deploy-locales.cjs" --deploy
node "Project\Overlay On-Screen Control\NVIDIA NodeAPI\tools\verify-live-locales.cjs" --reload-safe
```
- Deployed exactly 26 locale JSONs, the OSC sync script and `osc/index.html`: **28 files**.
- SHA-256 source/runtime verified for each; backup under `build_test/Launcher Localization_Test/PreviousVersions/Language-...`.
- Proof: `build_test/Launcher Localization_Test/LANGUAGE-DEPLOY-20261024.json`.
- Served HTTP 59011 contents checked against Build for **28 locales + script + HTML**; all matching.
- CEF remote debugging at 59099 verified reload while at safe `#/base`; after reload, `scriptNew=true`, active `th-TH`, `phraseCount=916`. No game, Hook or NvContainer restart.

## Tests
```powershell
node --test "Project\Overlay On-Screen Control\NVIDIA NodeAPI\tests\localeCoverage.test.js" "Project\Overlay On-Screen Control\NVIDIA NodeAPI\tests\launcherLanguageSync.test.js" "Project\Overlay On-Screen Control\NVIDIA NodeAPI\tests\oscUiLocalization.test.js"
node --test "Project\Launcher.Cef\tests\languageCoverage.test.cjs"
node --test "build_test\Launcher Localization_Test\launcher-i18n.test.cjs"
```
Locale tests pass. The **full NodeAPI regression remains red due to unrelated in-progress UI tests**, including `freestyleSettingsUi.test.js` (missing installer placeholder) and `mainMenuV5Overflow.test.js` (expected 40px inset rule). Do not change their UX contracts as part of localization.

## Remaining limitations
1. Missing locale entries have safe English fallbacks rather than verified translations. Translation QA is still needed for 26 other locales.
2. Launcher has its own additional strings which have English fallback, not 28 complete translation packs.
3. Language selection is not yet persisted in both directions; changing OSC language is respected during a running session but a restart can adopt the Launcher's last saved language again.
4. Dynamic hard-coded text in unrelated UI modules still needs human review rather than blindly translating game paths, icon glyphs, NVIDIA names or backend diagnostics.
