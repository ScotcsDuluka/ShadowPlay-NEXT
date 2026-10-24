# Build installation policy — ShadowPlay NEXT

**Default workflow for every completed change:** source → test → Release build → install to `build/NVIDIA ShadowPlay` → compare SHA-256 → verify runtime when safe. Do not leave a successful task only in `build_test` or a disposable worktree.

## Native NvContainer

Use `Scripts/Install-NvContainerBuild.ps1` after building the NvContainer Release bundle:

```powershell
& "C:\My Project\NVIDIA-Shadowplay\Scripts\Install-NvContainerBuild.ps1" `
  -SourceDirectory "C:\My Project\NVIDIA-Shadowplay\Project\NVIDIA Container\bin\Release\net10.0-windows10.0.26100.0"
```

`Scripts/build-dev.ps1` invokes this installer automatically after staging the other owned runtime files.

- When NvContainer **is not running**, copy the EXE/DLL/deps/runtimeconfig as a verified set into the actual Build path; save the previous version under `PreviousVersions`. If anything fails, attempt rollback from backup.
- When NvContainer **is running**, it has exclusive locks on the EXE and DLL. **Never partially overwrite only the unlocked JSON files, force-kill the supervisor, stop a game/hook, or claim active deployment.** Instead stage the complete verified bundle to `NVIDIA Container/PendingUpdates/NextInstall`; the installer exits with code 2 and the main build records a pending artifact. Run the same installer when the supervisor has exited normally to activate it.
- Exit code 0 means installed and SHA-256 verified. Exit code 2 means verified staging **only**, still waiting for a safe activation; other codes are failures.
- Native changes are **not hot reloaded** into a running process. Runtime activation requires the supervisor's next normal launch after installation.

## JS, CSS and OSC mods

Deploy changed static/runtime resources into their corresponding paths below `build/NVIDIA ShadowPlay`, verify checksums and cache-bust asset URLs when needed. A running OSC CEF page may need a normal reload to consume new scripts/styles; do not claim a live visual confirmation based only on Build copies.

## ApplicationOntology catalog

Source is `C:\My Project\nv-ontology-pages\public\catalog.json`. Treat that repository and the Cloudflare deployment separately from local builds. Preserve any existing uncommitted changes and never silently publish a partially verified catalog. Do not represent a local version as the already-published version.

## Verification

Keep the relevant tests with their source project. Record any unrelated pre-existing failures without changing neighboring work. Use `git diff --check` and file hashes for deployed artifacts. The currently running services are not restarted unless safe and explicitly intended by the task.
