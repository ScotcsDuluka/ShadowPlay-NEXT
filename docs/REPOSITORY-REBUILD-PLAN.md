# Repository Rebuild Plan — ShadowPlay-NEXT

**Status:** Proposed; planning only  
**Current repository:** `ScotcsDuluka/NVIDIA-Shadowplay`  
**Current default branch:** `Stable`  
**Intended destination for the newer CEF project:** `ShadowPlay-NEXT`

## Goal

Separate two project generations that currently coexist in the same repository:

1. **Legacy WinForms Overlay** — the older NVIDIA ShadowPlay-style desktop application and its established history.
2. **CEF Overlay / newer platform architecture** — the newer project built around CEF and a reorganized application/platform structure.

This is a repository-boundary plan, not a claim that the two systems are already independently buildable. Confirm dependency closure and buildability before moving source files.

## Current facts to preserve

- `Build/Build-Config/dev-layout.json` declares `Project` as the current build source root.
- `Build/Build-Config/dev-build.json` records the active project list and build output path for the last recorded development build.
- The current tree includes newer-platform areas such as `Project/NvContainer/`, `Project/NvGraphics/`, `Project/NvLauncher/Cef/`, `Project/Overlay OSC/NVIDIA NodeAPI/`, `Project/Overlay OSC/NVIDIA OSC/`, and `Project/Overlay OSC/NVIDIA OSC Adapter/`.
- These paths are **inventory candidates**, not an approved move list. Project references, linked source, build scripts, resources, configuration, and runtime contracts must be traced before extracting any of them.
- Existing history, releases, website/deployment workflows, and known-good legacy behavior must not be discarded during the split.

## Migration rules

1. **No mass move or deletion.** Keep the current repository intact until the destination can be built and checked independently.
2. **Map ownership first.** Classify files as legacy-only, NEXT-only, shared contract/infrastructure, original/upstream/reference material, or generated/local runtime artifacts.
3. **Trace dependencies.** Inspect every solution/project reference, linked source file, content/resource item, build manifest, post-build step, runtime path, IPC endpoint, and configuration schema that crosses the proposed boundary.
4. **Keep reference material separate.** Do not copy upstream NVIDIA binaries, source dumps, SDK payloads, credentials, personal logs, or generated artifacts into the new repository unless their inclusion is authorized, necessary, and documented.
5. **Define cross-repository contracts.** Decide how shared interfaces, IPC messages, configuration schemas, version compatibility, and build outputs are owned and versioned. Avoid permanent references to paths inside the old repository.
6. **Preserve rollback.** Perform the extraction on a dedicated branch and review it through a pull request. Keep the legacy project and its release path available until acceptance checks pass.
7. **Document provenance.** The new repository should explain that it is an independent, unofficial project and is not affiliated with, endorsed by, or sponsored by NVIDIA Corporation.

## Proposed phases

### Phase 0 — Baseline and inventory

- Record the source commit and active branch for each project generation.
- Confirm that local-only changes and runtime configuration have been accounted for.
- Inventory all solutions, project files, build manifests, tests, resources, runtime payloads, and publish/deploy workflows.
- Build a dependency map that identifies which source is actually consumed by each executable.

**Exit gate:** Every candidate file/folder has a documented owner and dependency classification; unknowns are explicitly listed.

### Phase 1 — Define repository boundaries

- Keep legacy WinForms source and its existing history in `NVIDIA-Shadowplay`.
- Define the exact source closure for `ShadowPlay-NEXT`; do not copy the entire `Project/` folder by default.
- Identify shared code that should become a versioned contract/package or be moved deliberately, rather than duplicated silently.
- Identify reference-only assets and exclude anything that must not be redistributed.

**Exit gate:** A file-level move/keep/share/reference manifest is reviewed before migration starts.

### Phase 2 — Establish ShadowPlay-NEXT

- Create the destination repository with a clear scope, setup instructions, supported build environment, and project-independence notice.
- Choose whether to preserve commit history for the extracted directories or start a clean history with a documented provenance link.
- Add a clean-checkout build workflow and verify that required authorized dependencies are available without relying on ignored files in the old working tree.

**Exit gate:** A clean clone can restore, build, and produce the expected outputs using documented steps.

### Phase 3 — Extract and validate

- Move only the approved NEXT source closure and required assets/configuration.
- Update solution paths, project references, relative paths, build scripts, packaging, and CI to the new repository layout.
- Test launch, process lifecycle, CEF/OSC integration, configuration loading, and any cross-process contracts on the intended Windows environment.
- Rebuild the legacy application from the original repository to catch accidental coupling.

**Exit gate:** NEXT builds and runs from a clean checkout; legacy still builds/runs; no hidden dependency on the old repository remains.

### Phase 4 — Stabilize

- Retain a documented migration/rollback point.
- Remove obsolete duplicates from the legacy repository only after explicit review and successful validation.
- Publish separate ownership and contribution instructions for both repositories.

## Definition of done

- [ ] The legacy WinForms project remains recoverable from its original repository history.
- [ ] `ShadowPlay-NEXT` contains only approved NEXT source, required dependencies/assets, and its own build/deploy instructions.
- [ ] A clean checkout of each repository builds independently in its documented environment.
- [ ] Runtime/configuration/IPC boundaries are documented and versioned.
- [ ] No secrets, unapproved upstream material, or incidental local build artifacts were copied.
- [ ] The README and repository names do not imply an official NVIDIA relationship.
- [ ] The owner has reviewed the migration manifest before any destructive cleanup.

## Explicit non-goals for this planning change

- No source code is moved or deleted by this document.
- No repository is renamed by this document.
- No architecture or runtime behavior is changed by this document.
