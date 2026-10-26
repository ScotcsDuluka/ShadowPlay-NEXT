# Intel analysis evidence — 2026-10-24

This directory contains local evidence generated on HUAWEI-MateBook while auditing commit `015f0548f82b5c1ed4d9c352fe095cc55e9a5453`. No real screen or audio source was opened. Production source is unchanged. `*.log` is ignored by the existing repository `.gitignore`; files remain locally available, and nothing has been staged or committed.

## Baseline commands and outcomes

Commands were run from `C:\My Project\NVIDIA-Shadowplay` with the installed `dotnet`:

```powershell
dotnet build 'Project\NVIDIA Capture\NVIDIA Capture.vbproj' -c Release --nologo -m:1 -v:minimal
```

Result: exit 0, 0 errors, 3 warnings (two SYSLIB0014, one BC42030). See `baseline-build.log`. This was an incremental build, not evidence that earlier assembly warnings were fixed.

The following projects ran with `dotnet run --project <path> -c Release` (the four small regression suites also used `--no-launch-profile -p:BuildInParallel=false -p:NuGetAudit=false`):

| Under `Project/NVIDIA Capture/EngineCapture/` | Checks | Result / evidence |
| --- | ---: | --- |
| `HardwareReadiness.Tests/HardwareReadiness.Tests.csproj`, no args, `--no-build` | 24 | PASS, `hardware-dryrun.log` |
| `RouteGuard.Tests/CaptureRouteGuard.Tests.vbproj`, no args | 33 | PASS, `route-guards.log` |
| `DisplaySelection.Tests/DisplaySelection.Tests.vbproj` | 16 | PASS, `display-selection.log` |
| `Routing.Tests/ShadowPlay.Capture.Routing.Tests.csproj` | 13 | PASS, `routing.log` |
| `Duluka Capture/Tests/Configuration/DulukaEngine.ConfigTests/DulukaEngine.ConfigTests.vbproj` | 95 | PASS, `configuration.log` |
| HardwareReadiness with `-- --dxgi-only` | Separate metadata mode | FAIL after successful inventory because it requires NVIDIA; `dxgi-inventory-nvidia-gate.log` |

Total passing selected baseline checks: **181**. The separate NVIDIA-only DXGI gate is a failure, not part of the pass count. First wrapped invocation reported exit 1; the later direct Windows process invocation recorded -532462766 for the same unhandled assertion. No full offline release gate was run.

## QSV probes

Bundled FFmpeg: `N-123522-gac4d50cb26-20260317`, `--enable-libvpl`. Encoder help reports NV12/QSV surfaces. Metadata help also reports ddagrab and gfxcapture, including WGC `hmonitor`; none of these help commands opens a capture source.

Initial console-only probe (exit 0; one packet):

```powershell
& '.\Project\FFmpeg\ffmpeg.exe' -hide_banner -nostdin -loglevel verbose -f lavfi -i 'color=c=black:s=128x128:r=1' -frames:v 1 -an -c:v h264_qsv -preset veryfast -f null -
```

Explicit adapter probe added the following before `-f lavfi`, also exit 0; retained log: `qsv-explicit-adapter-synthetic.log`:

```text
-init_hw_device qsv=iq:hw,child_device=0,child_device_type=d3d11va
```

This log identifies 8086:9B41 and MFX implementation 1.34. Adapter index 0 correlated with the contemporaneous read-only DXGI snapshot; the CLI did not report the actual device LUID.

The one-frame legacy rate-control probe used `-rc cbr -b:v 2000000` without explicit adapter selection. `qsv-legacy-rc-synthetic.log` reports the rc option unused and actual VBR, despite exit 0. This is a confirmed argument-semantics finding, not a decoder test.

## Synthetic MP4 comparison

All three comparison cases used the same explicit QSV child device and these base arguments, with an unused unique MP4 path under `%TEMP%\Duluka-Intel-Architecture-20261024`:

```text
-hide_banner -nostdin -loglevel verbose
-init_hw_device qsv=iq:hw,child_device=0,child_device_type=d3d11va
-f lavfi -i testsrc2=size=320x180:rate=30:duration=2
-frames:v 60 -an -c:v h264_qsv -preset veryfast
<rate arguments>
-bf 0 -async_depth 1 -look_ahead 0 -g 30 -pix_fmt nv12
-movflags +faststart -n <unique-output.mp4>
```

| Rate arguments | Actual mode from log | Encode / probe / full-decode exit | Declared / packets / decoded | File bytes |
| --- | --- | --- | --- | ---: |
| `-b:v 2000000 -maxrate 2000000 -bufsize 4000000` | CBR | 0 / 0 / -1094995529 | 60 / 60 / **28** | 276315 |
| `-b:v 2000000` | VBR | 0 / 0 / 0 | 60 / 60 / 60 | 236892 |
| none | CQP | 0 / 0 / 0 | 60 / 60 / 60 | 90947 |

All containers report duration 2.000000 s, H.264 320×180, average rate 30/1, and yuv420p. No audio stream exists. CBR emitted `Invalid NAL unit size` / input-NAL parsing errors. One case per setting was run; no generalized CBR root-cause conclusion is justified.

Probe command:

```text
ffprobe -v error -count_packets -count_frames -show_entries stream=codec_type,codec_name,width,height,pix_fmt,avg_frame_rate,nb_frames,nb_read_frames,nb_read_packets,duration:format=duration,size -of json <file>
```

Independent full decode:

```text
ffmpeg -hide_banner -nostdin -v error -xerror -i <file> -map 0:v:0 -f null -
```

CBR evidence files use prefix `qsv-synthetic-mp4-`; VBR/CQP use `qsv-vbr-` / `qsv-cqp-`. Each has encode log, probe JSON, decode log, and result TXT with absolute temporary-media path and exit statuses. The CBR probe's stderr appeared in the tool output rather than a separate file; its retained full-decode log contains the corruption evidence. VBR/CQP also retain probe stderr logs.

These synthetic MP4s are deliberately retained for investigation. They contain generated test patterns only. They are not Duluka-produced live recordings and do not prove display capture, game compatibility, audio, exact runtime LUID matching, sustained throughput, or native QSV integration.
