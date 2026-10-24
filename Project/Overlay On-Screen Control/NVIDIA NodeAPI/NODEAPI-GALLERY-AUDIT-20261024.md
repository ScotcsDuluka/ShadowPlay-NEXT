# NVIDIA NodeAPI — Gallery Audit & Runtime Fix (2026-10-24)

Scope: NodeAPI-owned Gallery backend and its HTTP interface. Coordinated Build/Restart authorized by user.

## Verified pre-fix findings
- `GET /Gallery/v.1.0/EnumerateDrives`, `Recent/6`, `PlaybackInfo` all returned HTTP 200; Gallery backend is not wholly offline.
- Previous `fallbackRecentFiles` called `readMediaFiles(videos)`, which listed **top-level files only**. Recording root has nested `Captures` and `NVIDIA` directories, so any recordings in those subfolders could be omitted from Recent.
- Native `shims/NvGalleryAPINode.js` is backed by generic-addon that defaults unimplemented commands to callback `{}`. `CopyFile` responded HTTP 200 `{}` for a deliberately nonexistent source path. Destination was **not** created: confirmed false success.
- Gallery fallback image thumbnail was real: `GetThumbnail` of an existing clip returned PNG data URI over HTTP 200. `PlaybackInfo` returned duration 50.76 seconds and audio presence.
- Fallback metadata responded with blank `duration` field. Not fixed in this pass (unknown native UI duration contract).
- Gallery playback relies on FFmpeg preparing WebM segments and browser MSE playback; a successful server response does **not** demonstrate CEF playback integration.

## Implementation
- `shims/gallery-recent-scanner.js`: bounded recursive list for recent media, sorted newest first, max depth 5, max folders 128, max candidates 4096, concurrent stat batch 16, max 100 result items. Skips symbolic links / reparse entries and inaccessible nested folders. Does not change files.
- `NvGalleryAPI.js`: recent listing uses the new scanner while keeping the existing HTTP response contract.
- `shims/gallery-copy-file.js`: validates source and absolute destination, uses existing `resolveRecordingMedia` to constrain source to configured recording folder, requires supported media extension and existing destination parent; `fs.copyFile(COPYFILE_EXCL)` prevents overwrites, replies success only after actual copy callback.
- `NvGalleryAPI.js`: CopyFile uses the actual, checked implementation; Remove now returns HTTP 501 `GALLERY_DELETE_UNAVAILABLE` rather than pretending a destructive no-op succeeded. No user media files were deleted.
- New isolated tests: `tests/galleryRecentScanner.test.js` and `tests/galleryCopyFile.test.js`.

## Validation
- Changed source JS: syntax PASS.
- Gallery-focused regression: **11/11 PASS** (includes existing `galleryThumbnail.test.js`).
- Only 3 NodeAPI files deployed: `NvGalleryAPI.js`, `shims/gallery-recent-scanner.js`, `shims/gallery-copy-file.js`.
- Existing Build versions backed up under `C:\My Project\NVIDIA-Shadowplay\Build\NodeAPI-GalleryBackup-20261024-105255`.
- Web Helper/NvNode restarted; health PASS. PIDs at verification: Web Helper **30456**, NvNode **6124** (may change under Container watchdog).
- Live `GET /Gallery/v.1.0/Recent/20`: HTTP 200.
- Live `POST /Gallery/v.1.0/CopyFile` with a **nonexistent source**: HTTP **404** (previously false HTTP 200); no destination file written.
- Live `POST /Gallery/v.1.0/Remove`: HTTP **501**, no deletion.
- Cross-origin Gallery GET: blocked HTTP **403**.
- Capture Status and NodeAPI Health unaffected: HTTP 200.
- Actual 8-sec WebM video segment from an existing clip: HTTP **200**, ~1.98 MB, 2.9 seconds.
- Actual matching audio segment: HTTP **200**, ~7.5 KB, 0.43 seconds.
- Metadata/thumbnail tested read-only; temporary FFmpeg caches may be populated; original recording unchanged.

## Remaining follow-ups
1. Native OSC Gallery UI and MSE playback not visually/interactive tested: seek, A/V sync, editor trimming, switching recordings, background lifecycle. These require CEF-level testing and possibly OSC UI owner.
2. `GetFileMetaData` fallback `duration` is empty; determine native frontend formatting/type contract before changing.
3. Delete currently deliberately unsupported (501) pending safe Recycle Bin semantics with verification / confirmation; do not implement permanent delete blindly.
4. Other generic native-addon placeholders (upload history, transcode, folder CRC) can still claim empty success. Audit and replace with honest unsupported status or real owner-backed operations.
5. Test gallery browsing with actual nested recordings (the present recording directory had 1 top-level media file and folders, but no nested media were established in live testing). Nested recursion behavior covered by fixtures.
6. Verify copy success through UI on a user-selected destination before release; unit tests prove filesystem copy only on temporary test fixtures.
