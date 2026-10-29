using System.Diagnostics;

if (args.Length > 0)
{
    Console.Write(Environment.GetEnvironmentVariable("NV_CAPTURE_DESIRED_BRIDGE"));
    return;
}

static void Check(bool value, string name)
{
    if (!value) throw new Exception(name);
    Console.WriteLine("PASS " + name);
}

Check(!CaptureControlEnvironment.Enabled(false, true, true, false), "default opt-in disabled");
Check(!CaptureControlEnvironment.Enabled(true, false, true, false), "runtime disabled");
Check(!CaptureControlEnvironment.Enabled(true, true, false, false), "gateway disabled");
Check(!CaptureControlEnvironment.Enabled(true, true, true, true), "test mode");
Check(CaptureControlEnvironment.Enabled(true, true, true, false), "fully enabled");
var worker = CaptureControlEnvironment.ForChild(true, true);
var host = CaptureControlEnvironment.ForChild(true, false);
Check(worker["NV_CAPTURE_CONTAINER_RECORD_COMMANDS"] == "1", "worker mode");
Check(host["NV_CAPTURE_DESIRED_BRIDGE"] == "1" && host["NV_CAPTURE_RUNTIME_ENABLED"] == "1", "host mode");
Check(CaptureControlEnvironment.ForChild(false, true)["NV_CAPTURE_CONTAINER_RECORD_COMMANDS"] == "0" &&
      CaptureControlEnvironment.ForChild(false, false)["NV_CAPTURE_DESIRED_BRIDGE"] == "0", "disabled mode");
var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true };
psi.ArgumentList.Add("child");
foreach (var setting in host) psi.Environment[setting.Key] = setting.Value;
using var child = Process.Start(psi)!;
var output = child.StandardOutput.ReadToEnd();
child.WaitForExit();
Check(child.ExitCode == 0 && output == "1", "child environment inheritance");
