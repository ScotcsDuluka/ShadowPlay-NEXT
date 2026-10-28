# Module Core

The profile generator is deterministic and has no dependencies beyond Node.js. Run from OSC directory:

    node Module/Core/build-profiles.cjs
    node --test Module/Core/test-profiles.cjs

Named profile generation:

    node Module/Core/build-profiles.cjs core-only

Generated independent CEF URL paths:
- /index.module.html
- /index.core-only.html
- /index.module-no-v5.html
- /index.module-no-motion.html

Do not use these pages in the live overlay until a controlled A/B test session; restoring /index.html switches back to the unchanged original Source page.

The module-runtime.js/ module-map.js scripts are loaded ONLY by generated diagnostic HTML, not by the existing index.html. Core-only attempts to block dynamic DOM insertion of extension scripts/links; it does not and cannot fully intercept arbitrary XHR/eval or bundled app code. Report exactly what loaded and what was blocked from window.__spnModuleAudit.snapshot().
