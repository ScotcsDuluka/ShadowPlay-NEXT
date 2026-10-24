using System;
using System.IO;
using System.Text.Json;
using System.Text;

int passed = 0;
void Check(string label, bool ok)
{
    if (!ok) throw new Exception("FAIL: " + label);
    ++passed;
    Console.WriteLine("PASS: " + label);
}
bool Denied(Action work)
{
    try { work(); return false; }
    catch (PairingSecretException) { return true; }
}
var area = Path.Combine(Path.GetTempPath(), "shadowplay-pairing-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(area);
var file = Path.Combine(area, PairingSecretStore.FileName);
try
{
    var previous = PairingSecretStore.NewSecret();
    var replacement = PairingSecretStore.NewSecret();
    Check("CSPRNG generates independent 128-bit secrets", previous != replacement &&
        PairingSecretStore.Valid(previous) && PairingSecretStore.Valid(replacement));
    Check("Malformed values rejected", !PairingSecretStore.Valid("abc") &&
        !PairingSecretStore.Valid(new string('X', 32)));
    var legacy = JsonSerializer.SerializeToElement(new { secret = previous });
    Check("Missing protected and legacy fails closed", Denied(() =>
        PairingSecretStore.Resolve(JsonSerializer.SerializeToElement(new {}), file)));
    Check("Legacy config still works before migration", PairingSecretStore.Resolve(legacy, file) == previous);
    Check("One-time legacy enrollment", PairingSecretStore.EnrollLegacy(legacy, file) == "enrolled");
    Check("DPAPI does not store plaintext", !Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains(previous));
    Check("Read protected same-user after restart", PairingSecretStore.Read(file) == previous);
    Check("Repeat migration is idempotent", PairingSecretStore.EnrollLegacy(legacy, file) == "already_enrolled");
    Check("Invalid candidate cannot replace vault", Denied(() => PairingSecretStore.Enroll(file, "bad")));
    Check("Wrong old secret cannot re-enroll", Denied(() => PairingSecretStore.Enroll(file, replacement)));
    PairingSecretStore.ReplaceOffline(file, replacement);
    Check("Explicit offline rotation changes protected content", PairingSecretStore.Read(file) == replacement);
    Check("Old legacy config is not used after rotation", PairingSecretStore.Resolve(legacy, file) == replacement);
    File.WriteAllText(file, "tampered");
    Check("Corrupt store fails without legacy fallback", Denied(() => PairingSecretStore.Resolve(legacy, file)));
    Check("No generated or secret value is included in failure output",
        Denied(() => PairingSecretStore.Read(file)));
    Console.WriteLine("PASS: " + passed + " isolated pairing security tests");
}
finally
{
    if (Directory.Exists(area)) Directory.Delete(area, true);
}
