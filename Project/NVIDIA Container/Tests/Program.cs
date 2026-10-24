using System;
using System.IO;
using System.Text.Json;

if (args.Length != 1 || !File.Exists(args[0]))
    throw new ArgumentException("Pass path to capture-engines.json.");

int passed = 0;
void Check(string name, bool result)
{
    if (!result) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    ++passed;
}
JsonElement Read(string policy, bool enabled, bool ready) =>
    JsonSerializer.SerializeToElement(CaptureEngineCatalog.Snapshot(policy, enabled, ready));

var data = Read(args[0], true, true);
Check("Stable policy valid", data.GetProperty("policyValid").GetBoolean());
Check("Readonly catalog", data.GetProperty("readOnly").GetBoolean());
Check("Selected route DXGI", data.GetProperty("selected").GetProperty("captureId").GetString() == "duluka:dxgi");
Check("Duluka wired", data.GetProperty("providers")[0].GetProperty("routeWired").GetBoolean());
Check("FFmpeg routed via existing legacy recording lane", data.GetProperty("providers")[1].GetProperty("routeWired").GetBoolean());
Check("FFmpeg readiness not inferred from source", !data.GetProperty("providers")[1].GetProperty("runtimeReady").GetBoolean());
Check("OBS not wired", !data.GetProperty("providers")[2].GetProperty("routeWired").GetBoolean());
Check("NvFBC not wired", !data.GetProperty("providers")[3].GetProperty("routeWired").GetBoolean());
Check("Disabled runtime not ready", !Read(args[0], false, true).GetProperty("captureRuntimeReady").GetBoolean());
Check("Worker unavailable not ready", !Read(args[0], true, false).GetProperty("captureRuntimeReady").GetBoolean());
var missing = Read(args[0] + ".missing", true, true);
Check("Missing policy fails closed", !missing.GetProperty("policyValid").GetBoolean());
Check("Missing policy never makes route ready", !missing.GetProperty("providers")[0].GetProperty("runtimeReady").GetBoolean());

string temporary = Path.GetTempFileName();
try
{
    File.WriteAllText(temporary, "{\"schemaVersion\":1,\"selectionLocked\":false}");
    var invalid = Read(temporary, true, true);
    Check("Unsafe selection policy invalid", !invalid.GetProperty("policyValid").GetBoolean());
}
finally { File.Delete(temporary); }
using var fakeWorkerDocument = JsonDocument.Parse(JsonSerializer.Serialize(new {
    schemaVersion = 2, readOnly = true,
    configured = new { backend = "FFmpeg", captureApi = "ddagrab",
        encoder = "libx264", allowed = true },
    actualSessionBackend = "FFmpeg", nativeRuntimeReady = false,
    nativeRuntimeCodec = "", recording = true,
    ffmpegBinary = new { binaryRunnable = true, captureApis = new[] { "ddagrab" },
        encoders = new[] { "libx264" }, hardwareEncoderVerified = false }
}));
var withWorker = JsonSerializer.SerializeToElement(CaptureEngineCatalog.Snapshot(
    args[0], true, true, fakeWorkerDocument.RootElement.Clone()));
Check("Worker truth included alongside locked policy",
    withWorker.GetProperty("workerRuntime").GetProperty("configured").GetProperty("backend").GetString() == "FFmpeg");
Check("Policy remains baseline, not live selection",
    withWorker.GetProperty("selected").GetProperty("policyOnly").GetBoolean());
Check("Worker capability recognized independently of policy",
    withWorker.GetProperty("workerTruthAvailable").GetBoolean());
Check("FFmpeg binary does not imply runtime ready",
    !withWorker.GetProperty("providers")[1].GetProperty("runtimeReady").GetBoolean());
Check("Missing policy does not suppress verified worker data",
    JsonSerializer.SerializeToElement(CaptureEngineCatalog.Snapshot(
        args[0] + ".missing", true, true, fakeWorkerDocument.RootElement.Clone()))
        .GetProperty("workerTruthAvailable").GetBoolean());
Check("Unverified fallback codec list is empty",
    data.GetProperty("providers")[0].GetProperty("encoders").GetArrayLength() == 0 &&
    data.GetProperty("providers")[1].GetProperty("encoders").GetArrayLength() == 0);
Check("Unknown recording state stays null",
    data.GetProperty("recording").ValueKind == JsonValueKind.Null);
using var incomplete = JsonDocument.Parse("{\"schemaVersion\":2,\"readOnly\":true,\"configured\":{}}");
Check("Incomplete worker schema cannot claim capabilities",
    !JsonSerializer.SerializeToElement(CaptureEngineCatalog.Snapshot(
        args[0], true, true, incomplete.RootElement.Clone()))
        .GetProperty("workerTruthAvailable").GetBoolean());
Console.WriteLine("PASS: " + passed + " capture catalog tests");
