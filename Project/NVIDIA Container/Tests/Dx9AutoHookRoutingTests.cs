using System;
using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

// Isolated routing tests. Synthetic PE binaries only; no game/Hooks touched.
var count = 0;
void Check(string name, bool value)
{
    if (!value) throw new Exception("FAIL " + name);
    Console.WriteLine("PASS " + name);
    count++;
}
Check("D3D11 stays compatible", OscAdapterLifecycle.SelectRenderApi(new[] { "d3d11" }) == "d3d11");
Check("DX9 single-family accepted", OscAdapterLifecycle.SelectRenderApi(new[] { "d3d9" }) == "d3d9");
Check("DX9Ex is never guessed from metadata", OscAdapterLifecycle.SelectRenderApi(new[] { "d3d9" }) != "d3d9ex");
Check("Unknown graphics API denied", OscAdapterLifecycle.SelectRenderApi(Array.Empty<string>()) == null);
Check("DX12 unsupported", OscAdapterLifecycle.SelectRenderApi(new[] { "d3d12" }) == null);
Check("DX9 plus DX11 ambiguous", OscAdapterLifecycle.SelectRenderApi(new[] { "d3d9", "d3d11" }) == null);
Check("Unsupported explicit DX9Ex catalog key denied", OscAdapterLifecycle.SelectRenderApi(new[] { "d3d9ex" }) == null);
Check("Legacy SupportsCurrentAdapter remains DX11 only",
    OscAdapterLifecycle.SupportsCurrentAdapter(new[] { "d3d11" }) &&
    !OscAdapterLifecycle.SupportsCurrentAdapter(new[] { "d3d9" }));
Check("SHA256 eligible", OscAdapterLifecycle.IsIdentityEligible("sha256", false));
Check("Name-only defaults denied", !OscAdapterLifecycle.IsIdentityEligible("executableName", false));
Check("Old DX11 development override remains explicit",
    OscAdapterLifecycle.IsIdentityEligible("executableName", true));
Check("Unknown matching mode denied even with override",
    !OscAdapterLifecycle.IsIdentityEligible("none", true));

var temp = Path.Combine(Path.GetTempPath(), "osc-dx9-container-route-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var host = Path.Combine(temp, "NVIDIA OSC Adapter.exe");
    Check("Missing Host fails closed", !OscAdapterLifecycle.HasDx9HostContract(host));
    void Binary(string path, ushort machine)
    {
        var file = new byte[512];
        file[0] = 0x4d; file[1] = 0x5a; // MZ
        BitConverter.GetBytes(0x80).CopyTo(file, 0x3c);
        file[0x80] = 0x50; file[0x81] = 0x45; // PE signature
        BitConverter.GetBytes(machine).CopyTo(file, 0x84);
        File.WriteAllBytes(path, file);
    }
    var subdir = Path.Combine(temp,"x86");
    Directory.CreateDirectory(subdir);
    Binary(host,0x8664);
    Binary(Path.Combine(temp,"NVIDIA OSC Adapter Hook.dll"),0x8664);
    Binary(Path.Combine(subdir,"NVIDIA OSC Adapter.exe"),0x014c);
    Binary(Path.Combine(subdir,"NVIDIA OSC Adapter Hook.dll"),0x014c);
    File.WriteAllBytes(Path.Combine(temp,"NVIDIA OSC Adapter.dll"),
        Enumerable.Repeat((byte)32,1024).Concat(Encoding.UTF8.GetBytes("--render-api")).ToArray());
    File.WriteAllBytes(Path.Combine(subdir,"NVIDIA OSC Adapter.dll"),
        Enumerable.Repeat((byte)32,1024).ToArray());
    Check("Old x86 managed Host prevents auto-hook", !OscAdapterLifecycle.HasDx9HostContract(host));
    File.AppendAllText(Path.Combine(subdir,"NVIDIA OSC Adapter.dll"),"--render-api");
    Check("Matching x64/x86 Host artifacts pass compatibility gate",
        OscAdapterLifecycle.HasDx9HostContract(host));

    var allowedPath = Path.Combine(temp, "DX9 Test Game.exe");
    var gameId = "test-controlled.dx9";
    using var scoped = new OscAdapterLifecycle(temp, true, "NVIDIA OSC Adapter.exe", "",
        0, _=>{}, allowUnverifiedIdentity:true, enableDx9AutoHook:true,
        dx9TestTargets: new[] { new Dx9TestTarget(gameId, allowedPath) });
    Check("Explicit game ID and executable path enable controlled DX9",
        scoped.IsAllowedDx9TestTarget(gameId, allowedPath));
    Check("Matching game ID but other executable path is denied",
        !scoped.IsAllowedDx9TestTarget(gameId, Path.Combine(temp, "Other.exe")));
    Check("Matching executable path but other game ID is denied",
        !scoped.IsAllowedDx9TestTarget("unknown.game", allowedPath));
    Check("Unscoped DX9 is denied with empty test target list",
        !(new OscAdapterLifecycle(temp, true, "NVIDIA OSC Adapter.exe", "",
            0, _=>{}, allowUnverifiedIdentity:true, enableDx9AutoHook:true))
            .IsAllowedDx9TestTarget(gameId, allowedPath));
    using var liveProcess = Process.GetCurrentProcess();
    var selfPath = Path.GetFullPath(liveProcess.MainModule!.FileName);
    var selfSig = liveProcess.Id + "|" + liveProcess.StartTime.ToUniversalTime().Ticks + "|" + selfPath;
    var validIdentity = new GameMatch(liveProcess.Id, selfSig, selfPath, Path.GetFileName(selfPath),
        gameId, "Control Test", "test", DateTime.UtcNow,
        new[] { "d3d9" }, "executableName");
    Check("Current PID, start-time and executable path are verified",
        OscAdapterLifecycle.IsCurrentProcessIdentity(validIdentity));
    Check("PID signature mismatch is denied",
        !OscAdapterLifecycle.IsCurrentProcessIdentity(validIdentity with { ProcessSignature = "stale" }));
    Check("Executable path mismatch is denied",
        !OscAdapterLifecycle.IsCurrentProcessIdentity(validIdentity with { ExecutablePath = "C:\\invalid\\not-game.exe" }));
    Binary(Path.Combine(subdir,"NVIDIA OSC Adapter Hook.dll"),0x8664);
    Check("Wrong-bitness native Hook prevents auto-hook",
        !OscAdapterLifecycle.HasDx9HostContract(host));
    Binary(Path.Combine(subdir,"NVIDIA OSC Adapter Hook.dll"),0x014c);
    // Runtime starts are disallowed; verify fields without launching a host.
    using var adapter = new OscAdapterLifecycle(temp, true, "NVIDIA OSC Adapter.exe", "",
        0, _=>{}, allowUnverifiedIdentity:true, enableDx9AutoHook:false);
    var snapshot=JsonSerializer.SerializeToElement(adapter.GetStatus());
    Check("DX9 rollout remains disabled even when Host matches",
        !snapshot.GetProperty("dx9AutoHookEligible").GetBoolean());
    Check("DX9 development override disables SHA requirement while auto-hook stays gated",
        !snapshot.GetProperty("dx9RequiresVerifiedIdentity").GetBoolean());
    using var strict = new OscAdapterLifecycle(temp, true, "NVIDIA OSC Adapter.exe", "",
        0, _=>{}, allowUnverifiedIdentity:false, enableDx9AutoHook:false);
    Check("Without override DX9 SHA gate remains enabled",
        JsonSerializer.SerializeToElement(strict.GetStatus())
            .GetProperty("dx9RequiresVerifiedIdentity").GetBoolean());
    Check("DX9Ex cannot claim runtime verification",
        !snapshot.GetProperty("dx9ExVerified").GetBoolean());
    Check("DX9 now accepts name-only matching when explicit development override is true",
        OscAdapterLifecycle.IsIdentityEligible("executableName", true));
    Check("DX9 still rejects name-only matching when override is false",
        !OscAdapterLifecycle.IsIdentityEligible("executableName", false));
    Check("Unknown or unmatched game is never permitted by DX9 override",
        !OscAdapterLifecycle.IsIdentityEligible("none", true));
}
finally { Directory.Delete(temp,true); }
Console.WriteLine("PASS " + count + " DX9/Ex Container routing tests; no process attached.");

internal sealed record GameMatch(
    int ProcessId, string ProcessSignature, string ExecutablePath, string ExecutableName, string GameId,
    string DisplayName, string CatalogVersion, DateTime MatchedUtc,
    IReadOnlyList<string> RenderApis, string MatchMethod);