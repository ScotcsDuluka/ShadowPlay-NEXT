using ShadowPlay.Capture.Routing;

if (args.Length == 1 && args[0] == "--list")
{
    Console.WriteLine("CAPTURE APIs");
    foreach (var c in BackendCatalog.Captures)
        Console.WriteLine($"  {c.Id,-22} {c.CaptureApi,-28} {c.Integration,-14} {c.Boundary}");
    Console.WriteLine("ENCODER APIs");
    foreach (var e in BackendCatalog.Encoders)
        Console.WriteLine($"  {e.Id,-22} {e.EncoderApi,-28} {e.Integration,-14} {e.Boundary}");
    return 0;
}

if (args.Length == 3 && args[0] == "--plan")
{
    RoutePlan plan = PipelineRouter.Plan(args[1], args[2]);
    Console.WriteLine($"Capture: {plan.CaptureId}");
    Console.WriteLine($"Encoder: {plan.EncoderId}");
    Console.WriteLine($"ContractCompatible: {plan.ContractCompatible}");
    Console.WriteLine($"RuntimeWired: {plan.RuntimeWired}");
    Console.WriteLine($"MayStart: {plan.MayStart}");
    Console.WriteLine(plan.Explanation);
    return plan.ContractCompatible ? 0 : 2;
}

Console.WriteLine("Usage: dotnet run -- --list | --plan <capture-id> <encoder-id>");
Console.WriteLine("This is an offline route validator, NOT a recording process.");
return args.Length == 0 ? 0 : 1;
