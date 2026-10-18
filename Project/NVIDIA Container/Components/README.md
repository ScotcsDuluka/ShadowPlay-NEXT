# NvContainer Components

Supporting implementation for the `NvContainer` host. The executable entry point remains `../NvContainer.cs`.

## Layout

- `Diagnostics/` — test/control API and diagnostic endpoints.
- `Routes/Gpu/` — GPU manager, profile, runtime, and executable-picker handlers.
- `Runtime/` — application catalog integration, game detection, OBS integration, and OSC adapter lifecycle.

## Naming rules

- Use short filenames aligned with the primary type name; the parent `NvContainer` project already provides the broader context.
- Keep `NvContainer` in the executable host name (`NvContainer.cs`), but avoid repeating it in every helper class and filename.
- Keep helper types grouped by responsibility and in the current global namespace until a namespace migration is planned separately.
- Keep runtime configuration and build output outside this source directory.
