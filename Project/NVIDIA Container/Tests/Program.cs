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
Check("FFmpeg not wired", !data.GetProperty("providers")[1].GetProperty("routeWired").GetBoolean());
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
Console.WriteLine("PASS: " + passed + " capture catalog tests");
