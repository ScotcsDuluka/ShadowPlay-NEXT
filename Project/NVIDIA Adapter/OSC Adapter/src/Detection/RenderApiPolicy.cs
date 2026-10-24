// OSC Adapter-owned routing policy. Native Hook remains the authority on
// whether a D3D9 device is Ex and whether real Present composition succeeds.
internal sealed record RenderApiDecision(bool Allowed, string Family, string Reason);

internal static class RenderApiPolicy
{
    internal static RenderApiDecision Resolve(string? requestedApi, IReadOnlySet<string>? loadedModules)
    {
        // Preserve the existing Container->Host D3D11 CLI ABI. Container must
        // explicitly opt in to the new API route after identity verification.
        if (string.IsNullOrWhiteSpace(requestedApi))
            return new(true, "legacy", "no-render-api-argument; existing D3D11 launch contract");

        var requested = requestedApi.Trim().ToLowerInvariant();
        if (requested is not ("auto" or "d3d9" or "d3d9ex" or "d3d11"))
            return new(false, "none", "unsupported-render-api");

        if (loadedModules is null)
            return new(false, "none", "runtime-module-evidence-unavailable");

        var has9 = loadedModules.Contains("d3d9.dll");
        var has11 = loadedModules.Contains("d3d11.dll");
        if (has9 && has11)
            return new(false, "none", "ambiguous-d3d9-and-d3d11-modules");

        if (requested == "auto")
        {
            if (has9)
                return new(true, "d3d9", "d3d9-module-present; Ex is verified only inside native Hook");
            if (has11)
                return new(true, "d3d11", "d3d11-module-present");
            return new(false, "none", "no-supported-runtime-module-evidence");
        }

        if (requested is "d3d9" or "d3d9ex")
        {
            if (has11)
                return new(false, "none", "requested-d3d9-but-d3d11-observed");
            // Allow early attachment *only* with an explicit trusted hint:
            // waiting for d3d9.dll may miss the device factory interception.
            return new(true, "d3d9", has9
                ? "d3d9-module-present; Ex requires native verification"
                : "early-attach-explicit-hint; no-renderer-module-yet");
        }

        if (has9)
            return new(false, "none", "requested-d3d11-but-d3d9-observed");
        return new(true, "d3d11", has11
            ? "d3d11-module-present"
            : "early-attach-explicit-hint; no-renderer-module-yet");
    }
}
