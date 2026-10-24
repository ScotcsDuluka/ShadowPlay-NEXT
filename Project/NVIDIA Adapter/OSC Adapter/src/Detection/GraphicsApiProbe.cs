using System.ComponentModel;
using System.Diagnostics;

// Passive runtime observation only. A loaded DLL does not prove which API
// actually presents frames. Renderer support is NEVER inferred from this.
internal sealed record GraphicsApiEvidence(string Diagnostic, IReadOnlySet<string>? Modules);

internal static class GraphicsApiProbe
{
    internal static GraphicsApiEvidence Inspect(Process game, string gameName)
    {
        string[] moduleNames;
        try
        {
            game.Refresh();
            moduleNames = game.Modules.Cast<ProcessModule>()
                .Select(module => module.ModuleName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Win32Exception ex)
        {
            return new GraphicsApiEvidence("API_PROBE_UNKNOWN game=" + gameName + " pid=" + game.Id +
                " reason=module-enumeration-error win32=" + ex.NativeErrorCode, null);
        }
        catch (InvalidOperationException)
        {
            return new GraphicsApiEvidence("API_PROBE_UNKNOWN game=" + gameName + " pid=" + game.Id +
                " reason=process-exited", null);
        }

        var modules = new HashSet<string>(moduleNames, StringComparer.OrdinalIgnoreCase);
        string[][] groups =
        {
            new[] { "D3D9", "d3d9.dll" },
            new[] { "D3D10", "d3d10.dll", "d3d10_1.dll" },
            new[] { "D3D11", "d3d11.dll" },
            new[] { "D3D12", "d3d12.dll", "d3d12core.dll" },
            new[] { "Vulkan", "vulkan-1.dll" },
            new[] { "OpenGL", "opengl32.dll" },
            new[] { "DXGI", "dxgi.dll" }
        };

        var observed = new List<string>();
        foreach (var group in groups)
        {
            var seen = group.Skip(1).Where(modules.Contains).ToArray();
            if (seen.Length == 0) continue;
            observed.Add(group[0] + "=" + string.Join("+", seen));
        }
        return new GraphicsApiEvidence("API_PROBE game=" + gameName + " pid=" + game.Id +
            " evidence=" + (observed.Count == 0 ? "none" : string.Join(",", observed)) +
            " confidence=module-presence-only renderer-verified=false", modules);
    }
}
