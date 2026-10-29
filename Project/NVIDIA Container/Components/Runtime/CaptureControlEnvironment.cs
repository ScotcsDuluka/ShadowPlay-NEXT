using System.Collections.Generic;

// One Container policy configures both children. Existing processes are not
// restarted; the NodeAPI must still verify the live worker's reported mode.
internal static class CaptureControlEnvironment
{
    internal static bool Enabled(bool optIn, bool runtimeEnabled, bool commandsAllowed, bool testMode) =>
        optIn && runtimeEnabled && commandsAllowed && !testMode;

    internal static Dictionary<string, string> ForChild(bool enabled, bool isWorker)
    {
        var environment = new Dictionary<string, string>
        {
            [isWorker ? "NV_CAPTURE_CONTAINER_RECORD_COMMANDS" : "NV_CAPTURE_DESIRED_BRIDGE"] = enabled ? "1" : "0"
        };
        // New NodeAPI control routes are explicitly runtime-gated.
        if (enabled && !isWorker) environment["NV_CAPTURE_RUNTIME_ENABLED"] = "1";
        return environment;
    }
}
