namespace ShadowPlay.Capture.Routing;

public sealed record RoutePlan(
    string CaptureId, string EncoderId, bool ContractCompatible,
    bool RuntimeWired, string Explanation)
{
    // Runtime must never treat a compatible PLAN as a runnable backend.
    public bool MayStart => ContractCompatible && RuntimeWired;
}

public static class PipelineRouter
{
    public static RoutePlan Plan(string? captureId, string? encoderId)
    {
        var capture = BackendCatalog.Captures.FirstOrDefault(
            x => string.Equals(x.Id, captureId, StringComparison.OrdinalIgnoreCase));
        if (capture is null)
            return new(captureId ?? "", encoderId ?? "", false, false, "Unknown capture API.");

        var encoder = BackendCatalog.Encoders.FirstOrDefault(
            x => string.Equals(x.Id, encoderId, StringComparison.OrdinalIgnoreCase));
        if (encoder is null)
            return new(capture.Id, encoderId ?? "", false, false, "Unknown encoder API.");

        if (capture.Integration != IntegrationStatus.SourcePresent ||
            encoder.Integration != IntegrationStatus.SourcePresent)
            return new(capture.Id, encoder.Id, false, false,
                "Backend is planned only; no production implementation is registered.");

        if (capture.Transport != encoder.Transport)
            return new(capture.Id, encoder.Id, false, false,
                $"Incompatible frame transport: {capture.Transport} -> {encoder.Transport}.");

        if (capture.Boundary != encoder.Boundary)
            return new(capture.Id, encoder.Id, false, false,
                $"Incompatible process ownership: {capture.Boundary} -> {encoder.Boundary}.");

        return new(capture.Id, encoder.Id, true, false,
            "Contract-level match only. Real factories, health probes, lifetime/PTS ownership " +
            "and host IPC must be wired and tested before enabling this route.");
    }
}
