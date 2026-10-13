# Realtime Engine Architecture

## Runtime topology

```text
UI / CaptureEngine process
│
├── AudioFileWriter
│   ├── WASAPI system loopback
│   ├── WASAPI microphone
│   ├── bounded PCM queue
│   └── dedicated WAV writer thread
│
└── ffmpeg.exe child #1 — VIDEO CAPTURE (realtime critical)
    └── ddagrab → NVENC → video.tmp.mp4

After recording stops:

CaptureEngine
│
└── ffmpeg.exe child #2 — MUX (offline/background work)
    └── video.tmp.mp4 + audio.wav → final.mp4
```

## Process responsibilities

### ffmpeg.exe #1 — Video capture

This process owns the realtime video path only:

- `ddagrab`
- NVENC
- video encoding
- temporary video output

It does **not** receive WASAPI audio input and does **not** run the final mux.

`JobObjectGuard` assigns the FFmpeg child to the Engine job and requests `AboveNormal` process priority on a best-effort basis. `High`/`Realtime` priority is intentionally avoided so Windows retains scheduler headroom for audio, UI, and system work.

### AudioFileWriter — Audio sidecar

Audio is intentionally kept out of the live FFmpeg command. WASAPI callbacks only copy/enqueue PCM; disk writes happen on the writer thread.

The audio sidecar records independently to temporary WAV files and reports explicit accounting:

```text
BytesEnqueued
WrittenBytes
DroppedBytes
DroppedSilenceBytes
BytesAccountingResidual
```

After a clean drain, the expected invariant is:

```text
BytesEnqueued = WrittenBytes + DroppedBytes + DroppedSilenceBytes
```

### ffmpeg.exe #2 — Mux

Mux starts only after the capture FFmpeg has exited and the WAV writer has drained.

- video stream is copied (`-c:v copy`)
- audio is encoded to AAC
- per-track offsets are applied
- `apad` handles short audio
- `-t` clamps output to measured video duration
- `+faststart` is applied to the final output

The temporary video file deliberately does **not** use `+faststart`: that flag would force an unnecessary second pass over an intermediate file at capture shutdown.

## Shutdown ordering

```text
STOP REQUEST
   │
   ▼
ffmpeg.exe #1 receives `q`
   │
   ├── finalize video.tmp.mp4
   │
   ▼
wait for capture process exit
   │
   ▼
AudioFileWriter.Stop()
   │
   ├── stop WASAPI
   ├── drain in-flight callbacks
   ├── drain writer queue
   └── finalize WAV headers
   │
   ▼
ffmpeg.exe #2 mux
   │
   ▼
final.mp4
```

## Non-goals

The realtime video process should not be burdened with:

- audio mixing
- audio resampling
- muxing
- waveform processing
- UI work
- synchronous filesystem work outside its own video output

## Current performance target

For a configured 144 FPS capture, the capture process is the critical path. Runtime evidence should be evaluated independently for:

- video `fps` / `speed`
- FFmpeg `dup` / `drop`
- audio queue drops
- audio writer lag
- accounting residual
- final A/V sync

A healthy audio run must not be used as proof that video source timestamps are healthy; `dup/drop` must be diagnosed separately.
