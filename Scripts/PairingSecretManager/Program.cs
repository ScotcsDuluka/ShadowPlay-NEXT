using System;
using System.IO;
using System.Text.Json;

// Manual enrollment only; no plaintext in arguments or logs.
// Usage: audit|migrate|generate CONFIG_PATH [--acknowledge]
if (args.Length < 2 || args.Length > 3 ||
    (args.Length == 3 && args[2] != "--acknowledge"))
{
    Console.Error.WriteLine("Usage: audit|migrate|generate <nvcontainer.json> [--acknowledge]");
    return 2;
}
string mode = args[0], configPath = args[1], vault = PairingSecretStore.DefaultPath;
if (!File.Exists(configPath))
{
    Console.Error.WriteLine("config_missing");
    return 2;
}
try
{
    using var document = JsonDocument.Parse(File.ReadAllText(configPath));
    if (!document.RootElement.TryGetProperty("pairing", out var pairing))
        throw new PairingSecretException("pairing_config_missing");
    bool hasLegacy = pairing.TryGetProperty("secret", out var field) &&
        field.ValueKind == JsonValueKind.String &&
        PairingSecretStore.Valid(field.GetString());
    if (mode == "audit")
    {
        bool protectedStore = File.Exists(vault);
        bool validStore = false;
        if (protectedStore) { _ = PairingSecretStore.Read(vault); validStore = true; }
        Console.WriteLine("pairingLegacyPresent=" + hasLegacy);
        Console.WriteLine("protectedStorePresent=" + protectedStore);
        Console.WriteLine("protectedStoreReadable=" + validStore);
        Console.WriteLine("action=none");
        return 0;
    }
    if (args.Length != 3)
        throw new PairingSecretException("explicit_acknowledgement_required");
    if (mode == "migrate")
    {
        if (!hasLegacy) throw new PairingSecretException("legacy_secret_missing");
        Console.WriteLine("pairingEnrollment=" + PairingSecretStore.EnrollLegacy(pairing, vault));
        Console.WriteLine("valueChanged=false");
        return 0;
    }
    if (mode == "generate")
    {
        if (hasLegacy) throw new PairingSecretException("legacy_secret_present");
        if (File.Exists(vault)) throw new PairingSecretException("protected_secret_already_exists");
        Console.WriteLine("pairingEnrollment=" + PairingSecretStore.Enroll(vault, PairingSecretStore.NewSecret()));
        Console.WriteLine("newInstallOnly=true");
        return 0;
    }
    throw new PairingSecretException("unknown_action");
}
catch (PairingSecretException ex)
{
    Console.Error.WriteLine("pairingError=" + ex.Message);
    return 3;
}
catch
{
    Console.Error.WriteLine("pairingError=unexpected_failure");
    return 4;
}
