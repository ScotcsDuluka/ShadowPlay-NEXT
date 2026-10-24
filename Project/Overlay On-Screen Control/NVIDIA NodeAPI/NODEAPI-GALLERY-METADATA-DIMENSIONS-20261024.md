# NVIDIA NodeAPI — Gallery Metadata & Image Dimensions Fix
Date: 2026-10-24
Owner: NVIDIA NodeAPI

## Verified problems
- Gallery's `POST /Gallery/v.1.0/GetFileMetaData` returned a successful response for real video, but `duration: ""` was hard-coded by `fallbackFileMetadata`. The OSC's minified `osc/app.js` renders `file.duration` and downstream operations split duration on `:` into three HH:MM:SS components.
- `POST /Gallery/v.1.0/GetImageFileDimensions` returned HTTP 200 `{}` even for a real 32x32 PNG. The native `NvGalleryAPINode` shim has no genuine image dimension implementation.
- Gallery still has separate unfinished workflows for media editing, deletion, and native Gallery actions. This fix only changes read-only metadata and image dimensions.

## Changes
1. Added `shims/gallery-media-metadata.js`. Uses bundled `ffprobe.exe` only (bounded 4.5-second timeout; 64 KiB stdout) to read format.duration without transcoding or modifying recordings. Converts seconds to zero-padded HH:MM:SS, caches by filename + file size + mtime, capped at 512 entries. Invalid/unavailable metadata never claims a duration.
2. Integrated in `NvGalleryAPI.js` fallback `GetFileMetaData`: thumbnail is generated as before; video metadata duration is enriched by FFprobe. Images and unavailable FFmpeg retain safe behavior.
3. Added `shims/gallery-image-dimensions.js`: bounded 256 KiB file-header read, validating PNG/GIF/BMP/JPEG image dimensions. Supports Windows files using absolute paths, rejects missing/invalid files with HTTP 4xx instead of fake HTTP 200 success. No image decoding, writing or transformation.
4. Replaced `GalleryGetImageDimensions` native shim call in `NvGalleryAPI.js` with the new local header parser.
5. Tests: `tests/galleryMediaMetadata.test.js` (5 cases) and `tests/galleryImageDimensions.test.js` (7 cases). Focused Gallery/Security regression: **28/28 pass**.

## Deployment / rollback
- Stage 1: `NvGalleryAPI.js`, `shims/gallery-media-metadata.js` copied Source -> Build; backups `C:\My Project\NVIDIA-Shadowplay\Build\NodeAPI-GalleryMetadataBackup-20261024-114100`.
- Stage 2: `NvGalleryAPI.js`, `shims/gallery-image-dimensions.js` copied Source -> Build; backups `C:\My Project\NVIDIA-Shadowplay\Build\NodeAPI-GalleryDimensionsBackup-20261024-114419`.
- SHA-256 verified that all three relevant source files match deployed Build.
- Most recent restart (at validation): managed NVIDIA Web Helper PID **32812**, child NvNode.exe PID **33952**; `GET :59011/Backend/v.1.0/health` HTTP 200. PIDs can change.

## Live runtime acceptance
- Real MP4 test clip: `POST /Gallery/v.1.0/GetFileMetaData` -> HTTP 200; `fileType:"video"`, `duration:"00:00:50"`, thumbnail present.
- Same request after first probe: ~5 ms vs ~416 ms initial request; timings from local test, not guaranteed across devices/files.
- Real 32 x 32 PNG: `POST /Gallery/v.1.0/GetImageFileDimensions` -> HTTP 200 `{"width":32,"height":32}` (previously 200 `{}`).
- Missing image: HTTP 404 (not fake successful `{}`).
- `GET /Gallery/v.1.0/Recent/10` -> HTTP 200, includes an actual recording nested under `Videos\NVIDIA\Floating Point`; verifies previous recursive Gallery scan with real nested content.
- `GET /Backend/v.1.0/health` HTTP 200.
- External Origin request to Gallery blocked HTTP 403.
- Copy of nonexisting source -> HTTP 404, no destination; safe-stub Remove -> HTTP 501; previous Gallery fixes remain.
- No original recording or screenshot was deleted, renamed, copied or edited in this pass; metadata and file-header reads only.

## Known limitations / next owner work
- Real in-OSC CEF Gallery UX is NOT fully verified: image viewer, video play/seek, audio/video synchronization, editor timeline and upload flows.
- `Gallery MediaSource` playback uses separate video/audio WebM segments with FFmpeg transcoding; earlier tests proved segment HTTP 200 but not seamless visual playback.
- `Gallery/v.1.0/Remove` intentionally returns HTTP 501 until secure user-confirmed Recycle Bin support is implemented. Don't silently make it permanent delete.
- Other native shims may still return empty success: upload history, transcode, folder CRC etc. Audit these before claiming full Gallery functional completion.
