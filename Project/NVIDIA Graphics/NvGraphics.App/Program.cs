namespace NvGraphics;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var instanceMutex = new System.Threading.Mutex(
            initiallyOwned: true,
            name: @"Local\NVIDIA.ShadowPlay.NvGraphics",
            createdNew: out var createdNew);
        if (!createdNew)
            return;

        var controlApiPort = 59012;
        var startHidden = false;
        foreach (var arg in args)
        {
            const string prefix = "--control-api-port=";
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(arg.Substring(prefix.Length), out var parsedPort) &&
                parsedPort is > 0 and <= 65535)
                controlApiPort = parsedPort;
            if (string.Equals(arg, "--start-hidden", StringComparison.OrdinalIgnoreCase))
                startHidden = true;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new NvGraphicsForm(controlApiPort, startHidden));
    }
}
