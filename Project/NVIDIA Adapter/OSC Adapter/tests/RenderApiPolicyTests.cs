using System.Collections.Generic;
static IReadOnlySet<string> Modules(params string[] names)
    => new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
static void Check(string name, string? requested, IReadOnlySet<string>? modules,
    bool allowed, string family)
{
    var decision = RenderApiPolicy.Resolve(requested, modules);
    if (decision.Allowed != allowed || decision.Family != family)
        throw new Exception(name + ": expected " + allowed + "/" + family +
            ", got " + decision.Allowed + "/" + decision.Family + " (" + decision.Reason + ")");
    Console.WriteLine("PASS " + name + " - " + decision.Reason);
}
Check("legacy-D3D11-ABI", null, null, true, "legacy");
Check("runtime-DX9", "auto", Modules("d3d9.dll"), true, "d3d9");
Check("runtime-DX11", "auto", Modules("d3d11.dll"), true, "d3d11");
Check("auto-without-evidence", "auto", Modules(), false, "none");
Check("auto-with-unreadable-modules", "auto", null, false, "none");
Check("ambiguous-middleware", "auto", Modules("d3d9.dll", "d3d11.dll"), false, "none");
Check("requested-DX9", "d3d9", Modules("d3d9.dll"), true, "d3d9");
Check("requested-DX9Ex", "d3d9ex", Modules("d3d9.dll"), true, "d3d9");
Check("requested-DX9Ex-early", "d3d9ex", Modules(), true, "d3d9");
Check("requested-DX11-early", "d3d11", Modules(), true, "d3d11");
Check("DX9-hint-conflicts", "d3d9", Modules("d3d11.dll"), false, "none");
Check("DX11-hint-conflicts", "d3d11", Modules("d3d9.dll"), false, "none");
Check("DX9Ex-both-conflict", "d3d9ex", Modules("d3d9.dll","d3d11.dll"), false, "none");
Check("unreadable-module-list", "d3d9", null, false, "none");
Check("unsupported-api", "d3d12", Modules("d3d12.dll"), false, "none");
Check("unknown-api", "vulkan", Modules("vulkan-1.dll"), false, "none");
Console.WriteLine("PASS OSC Adapter Host routing policy tests: 16 scenarios");
