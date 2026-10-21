namespace ShadowPlay.Capture.Routing;

// Routing metadata only: source availability is NOT a hardware/runtime health check.
public enum RuntimeBoundary { InProcess, FfmpegChild, ObsExternal }
public enum FrameTransport { D3D11Texture, FfmpegInternal, ObsManaged }
public enum IntegrationStatus { SourcePresent, Planned }

public sealed record CaptureEndpoint(
    string Id, string Provider, string CaptureApi, FrameTransport Transport,
    RuntimeBoundary Boundary, IntegrationStatus Integration, string Note);

public sealed record EncoderEndpoint(
    string Id, string Provider, string EncoderApi, FrameTransport Transport,
    RuntimeBoundary Boundary, IntegrationStatus Integration, string Note);

public static class BackendCatalog
{
    public static IReadOnlyList<CaptureEndpoint> Captures { get; } =
    [
        new("duluka:dxgi", "Duluka", "DXGI Desktop Duplication",
            FrameTransport.D3D11Texture, RuntimeBoundary.InProcess,
            IntegrationStatus.SourcePresent, "DdagrabBackend source exists; runtime probe required"),
        new("duluka:wgc", "Duluka", "Windows Graphics Capture",
            FrameTransport.D3D11Texture, RuntimeBoundary.InProcess,
            IntegrationStatus.Planned, "WgcCapture exists for overlay; game-capture adapter is not wired"),
        new("ffmpeg:ddagrab", "FFmpeg", "FFmpeg ddagrab",
            FrameTransport.FfmpegInternal, RuntimeBoundary.FfmpegChild,
            IntegrationStatus.SourcePresent, "FFmpeg command builder exists; FFmpeg binary/capabilities must be probed"),
        new("ffmpeg:gdigrab", "FFmpeg", "FFmpeg gdigrab",
            FrameTransport.FfmpegInternal, RuntimeBoundary.FfmpegChild,
            IntegrationStatus.SourcePresent, "FFmpeg command builder exists; FFmpeg binary/capabilities must be probed"),
        new("obs:managed", "OBS", "OBS-owned capture",
            FrameTransport.ObsManaged, RuntimeBoundary.ObsExternal,
            IntegrationStatus.Planned, "OBS WebSocket control/recording bridge not implemented"),
        new("nvidia:nvfbc", "NVIDIA", "NVIDIA NvFBC",
            FrameTransport.D3D11Texture, RuntimeBoundary.InProcess,
            IntegrationStatus.Planned, "Do not assume NvFBC availability or licensing on consumer GPUs")
    ];

    public static IReadOnlyList<EncoderEndpoint> Encoders { get; } =
    [
        new("nvidia:nvenc-h264", "NVIDIA", "NVENC H.264 (native)",
            FrameTransport.D3D11Texture, RuntimeBoundary.InProcess,
            IntegrationStatus.SourcePresent, "DulukaEngine.Encoder.Nvenc code exists; device probe required"),
        new("ffmpeg:nvenc-h264", "FFmpeg", "FFmpeg h264_nvenc",
            FrameTransport.FfmpegInternal, RuntimeBoundary.FfmpegChild,
            IntegrationStatus.SourcePresent, "FFmpeg owns its internal frames and encoder"),
        new("ffmpeg:libx264", "FFmpeg", "FFmpeg libx264",
            FrameTransport.FfmpegInternal, RuntimeBoundary.FfmpegChild,
            IntegrationStatus.SourcePresent, "FFmpeg binary must expose libx264 at runtime"),
        new("obs:managed", "OBS", "OBS-managed encoder",
            FrameTransport.ObsManaged, RuntimeBoundary.ObsExternal,
            IntegrationStatus.Planned, "Controlled by OBS, not an interchangeable in-process encoder")
    ];
}
