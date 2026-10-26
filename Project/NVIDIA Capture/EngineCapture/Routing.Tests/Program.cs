using ShadowPlay.Capture.Routing;

int passed = 0;
void Test(string name, bool ok)
{
    if (!ok) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}

var d = PipelineRouter.Plan("duluka:dxgi", "nvidia:nvenc-h264");
Test("DXGI -> native NVENC transport matches", d.ContractCompatible);
Test("Route cannot start without real wiring", !d.MayStart && !d.RuntimeWired);

var f = PipelineRouter.Plan("ffmpeg:ddagrab", "ffmpeg:nvenc-h264");
Test("FFmpeg-owned capture and encode match", f.ContractCompatible);
Test("FFmpeg route not activated by a plan", !f.MayStart);

var g = PipelineRouter.Plan("ffmpeg:gdigrab", "ffmpeg:libx264");
Test("FFmpeg software encoder pair matches", g.ContractCompatible);

var mismatch = PipelineRouter.Plan("ffmpeg:ddagrab", "nvidia:nvenc-h264");
Test("Cross-process incompatible frame handoff rejected",
    !mismatch.ContractCompatible && mismatch.Explanation.Contains("transport"));

Test("OBS bridge not reported as implemented",
    !PipelineRouter.Plan("obs:managed", "obs:managed").ContractCompatible);
Test("NvFBC not assumed available",
    !PipelineRouter.Plan("nvidia:nvfbc", "nvidia:nvenc-h264").ContractCompatible);
Test("WGC overlay source not promoted to game capture",
    !PipelineRouter.Plan("duluka:wgc", "nvidia:nvenc-h264").ContractCompatible);
Test("Intel native composition remains planned and non-runnable",
    !PipelineRouter.Plan("duluka:dxgi", "intel:qsv-h264").MayStart &&
    !PipelineRouter.Plan("duluka:dxgi", "intel:qsv-h264").ContractCompatible);
Test("Unknown capture rejected", !PipelineRouter.Plan("wrong", "nvidia:nvenc-h264").ContractCompatible);
Test("Unknown encoder rejected", !PipelineRouter.Plan("duluka:dxgi", "wrong").ContractCompatible);

var ids = BackendCatalog.Captures.Select(x => x.Id);
Test("No duplicate capture IDs", ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() == ids.Count());
var encIds = BackendCatalog.Encoders.Select(x => x.Id);
Test("No duplicate encoder IDs", encIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() == encIds.Count());

Console.WriteLine($"PASS: {passed} routing tests");
