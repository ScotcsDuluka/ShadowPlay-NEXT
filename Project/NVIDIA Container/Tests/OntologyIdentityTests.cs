using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length != 1) throw new Exception("Supply public/catalog.json from the local repo");
var original = File.ReadAllText(args[0]);
var checks = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception("FAIL: " + name);
    ++checks;
    Console.WriteLine("PASS " + name);
}
OntologyCatalogSnapshot Parse(string text)
{
    var file = Path.Combine(Path.GetTempPath(), "ontology-identity-test-" +
        Guid.NewGuid().ToString("N") + ".json");
    File.WriteAllText(file, text);
    using var client = new ApplicationOntologyClient(
        "https://nv-ontology.pages.dev/api/", 3, file, _ => {});
    var snapshot = client.LoadCachedCatalog();
    File.Delete(file);
    return snapshot;
}
using var baseClientFile = new TempCatalog(original);
using var client = new ApplicationOntologyClient(
    "https://nv-ontology.pages.dev/api/", 3, baseClientFile.Path, _ => {});
var local = client.LoadCachedCatalog();
Check(local.Ready && local.GameCount >= 6, "Expanded game catalog validates");
if (local.CatalogVersion == "1.2.8")
{
    Check(local.GameCount == 8, "ApplicationOntology v1.2.8 includes eight games");
    var floating = client.ResolveExecutable("Floating Point.exe");
    var frogs = client.ResolveExecutable("100_hidden_frogs.exe");
    Check(floating is { Matched: true } && floating.RenderApis.SequenceEqual(new[] { "d3d9" }),
        "Floating Point is declared DX9 without verified active renderer");
    Check(frogs is { Matched: true } && frogs.RenderApis.SequenceEqual(new[] { "d3d9" }),
        "100 hidden frogs stays DX9 family, never inferred as DX9Ex");
    Check(floating.MatchMethod != "sha256" && frogs.MatchMethod != "sha256",
        "Unpinned DX9 games cannot claim SHA-256 verified game identity");
}
var among = client.ResolveExecutable("Among Us.exe");
Check(among is { Matched: true, MatchMethod: "executableName" }, "Among Us maps by name");
Check(among.RenderApis.SequenceEqual(new [] {"d3d11"}), "New local Among Us DX11 metadata retained");
Check(!OscAdapterLifecycle.SupportsCurrentAdapter(Array.Empty<string>()), "Unknown renderer fails closed");
Check(!OscAdapterLifecycle.SupportsCurrentAdapter(new [] {"d3d12"}), "DX12 cannot enter D3D11 hook");
Check(!OscAdapterLifecycle.SupportsCurrentAdapter(new [] {"d3d9"}), "DX9 cannot enter D3D11 hook");
Check(!OscAdapterLifecycle.SupportsCurrentAdapter(new [] {"d3d9","d3d11"}), "Mixed renderer cannot enter D3D11 hook");
Check(OscAdapterLifecycle.SupportsCurrentAdapter(among.RenderApis), "Exclusive DX11 metadata allows eligible adapter");
Check(!OscAdapterLifecycle.IsIdentityEligible("executableName", false), "Unverified executable cannot enter hook by default");
Check(OscAdapterLifecycle.IsIdentityEligible("sha256", false), "SHA-256 verified identity is hook eligible");
Check(OscAdapterLifecycle.IsIdentityEligible("executableName", true), "Development override explicitly permits name-only identity");
Check(!OscAdapterLifecycle.IsIdentityEligible("none", true), "Override never permits unmatched process");
Check(client.ResolveExecutable("Among Us.exe", new string('a',64)) is
    {Matched:false,Reason:"hash_unverified"}, "Supplied unpinned hash never claims verification");
Check(client.ResolveExecutable("fake.exe") is {Matched:false,Reason:"not_found"}, "Unknown EXE rejected");
var mutable = JsonNode.Parse(original);
mutable["entries"][0]["sha256"] = new JsonArray(JsonValue.Create(new string('a',64)));
using var pin = new TempCatalog(mutable.ToJsonString());
using var pinnedClient = new ApplicationOntologyClient("https://nv-ontology.pages.dev/api/",3,pin.Path,_=>{});
Check(pinnedClient.ResolveExecutable("Dungeons-Win64-Shipping.exe") is
    {Matched:false,Reason:"hash_required"}, "Pinned catalog requires SHA-256");
Check(pinnedClient.ResolveExecutable("Dungeons-Win64-Shipping.exe",new string('b',64)) is
    {Matched:false,Reason:"hash_mismatch"}, "Pinned catalog rejects wrong SHA-256");
Check(pinnedClient.ResolveExecutable("Dungeons-Win64-Shipping.exe",new string('a',64)) is
    {Matched:true,MatchMethod:"sha256"}, "Correct digest verifies unique executable");
mutable["entries"][0]["renderApis"] = new JsonArray(JsonValue.Create("d3d13"));
Check(!Parse(mutable.ToJsonString()).Ready, "Invalid render API fails catalog validation");
Console.WriteLine("SUCCESS " + checks + " native ontology identity tests");

var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
    "ontology-watcher-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
try
{
    var executable = System.IO.Path.Combine(tempDir, "Dungeons-Win64-Shipping.exe");
    File.WriteAllBytes(executable, System.Text.Encoding.UTF8.GetBytes(
        "read-only game-watcher digest fixture, not an executable"));
    var correctHash = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(executable)));
    var withPin = JsonNode.Parse(original);
    withPin["entries"][0]["sha256"] = new JsonArray(JsonValue.Create(correctHash));
    using var pinnedFile = new TempCatalog(withPin.ToJsonString());
    using var pinnedWatcherCatalog = new ApplicationOntologyClient(
        "https://nv-ontology.pages.dev/api/", 3, pinnedFile.Path, _ => {});
    using var adapter = new OscAdapterLifecycle(tempDir, false, "", "", 0, _ => {});
    using var watcher = new GameWatcher(pinnedWatcherCatalog, adapter,
        false, 1, 10, _ => {}, _ => {});

    var type = typeof(GameWatcher).GetNestedType("ObservedProcess",
        System.Reflection.BindingFlags.NonPublic);
    Check(type != null, "GameWatcher observed-process test model found");
    var candidate = Activator.CreateInstance(type, new object[] {
        777, "signature-777", executable,
        "Dungeons-Win64-Shipping.exe", DateTime.UtcNow
    });
    var listType = typeof(System.Collections.Generic.List<>).MakeGenericType(type);
    var candidates = Activator.CreateInstance(listType);
    listType.GetMethod("Add").Invoke(candidates, new[] {candidate});
    var method = typeof(GameWatcher).GetMethod("ResolveCandidatesAsync",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    await (Task)method.Invoke(watcher, new object[]{candidates, CancellationToken.None});
    var field = typeof(GameWatcher).GetField("_resolutionCache",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    var cache = (System.Collections.IDictionary)field.GetValue(watcher);
    var decision = cache["signature-777"];
    Check(decision != null &&
        (bool)decision.GetType().GetProperty("Matched").GetValue(decision) &&
        (string)decision.GetType().GetProperty("MatchMethod").GetValue(decision) == "sha256",
        "GameWatcher hashes the actual pinned executable and validates it");
}
finally
{
    Directory.Delete(tempDir, true);
}

sealed class TempCatalog : IDisposable
{
    public string Path {get;}
    public TempCatalog(string text)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "ontology-identity-test-"+Guid.NewGuid().ToString("N")+".json");
        File.WriteAllText(Path,text);
    }
    public void Dispose() { if(File.Exists(Path))File.Delete(Path); }
}
