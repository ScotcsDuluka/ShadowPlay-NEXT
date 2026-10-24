using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

int passed = 0;
void Check(string name, bool condition)
{
    if (!condition) throw new Exception("FAIL "+name);
    Console.WriteLine("PASS "+name);
    passed++;
}
JsonElement JSON(string json) => JsonDocument.Parse(json).RootElement.Clone();
JsonElement AsJson(object body) => JsonSerializer.SerializeToElement(body);
const string health = """{"ready":true,"recording":false}""";
const string state = """{"ready":true,"recording":false,"actualSessionBackend":"","elapsedSec":0}""";
const string displays = """{"schemaVersion":1,"readOnly":true,"source":"NvCapture","displays":[{"OutputIndex":2,"AdapterLuidLow":987,"AdapterLuidHigh":-12,"Width":1920,"Height":1080,"Primary":true,"Label":"Display"}],"selected":{"OutputIndex":2,"AdapterLuidLow":987,"AdapterLuidHigh":-12},"selectionValid":true,"error":"","recording":false,"appliesOnEngineRebuild":true}""";
const string selected = """{"saved":true,"selected":{"OutputIndex":2,"AdapterLuidLow":987,"AdapterLuidHigh":-12},"appliesOnEngineRebuild":true,"effectiveNow":false}""";
const string screenshot = """{"Saved":true,"File":"test.png","ErrorCode":"","DisplayIndex":2}""";
var getPaths = new List<string>();
var postPaths = new List<string>();
var bodies = new List<string>();
Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> handler = async (req, _) =>
{
    var path = req.RequestUri!.AbsolutePath;
    if (req.Method == HttpMethod.Get) getPaths.Add(path);
    else {
        postPaths.Add(path);
        bodies.Add(await req.Content!.ReadAsStringAsync());
    }
    string response = path switch {
        "/health" => health, "/state" => state, "/displays" => displays,
        "/display/select" => selected, "/screenshot" => screenshot,
        _ => """{"error":"not_found"}"""
    };
    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
};
CaptureWorkerGateway Create(
    Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>>? responder = null,
    bool displayWrite = false, bool shotWrite = false, bool testMode = false) =>
    new(new HttpClient(new Fake(responder ?? handler)), 59990,
        enabled:true, allowRecordCommands:false, testMode:testMode,
        allowDisplayCommands:displayWrite, allowScreenshotCommands:shotWrite);
var yes = JSON("""{"confirmed":true}""");
var no = JSON("""{"confirmed":false}""");
var chosen = JSON("""{"confirmed":true,"outputIndex":2,"adapterLuidLow":987,"adapterLuidHigh":-12}""");
var untrusted = JSON("""{"confirmed":true,"outputIndex":3,"adapterLuidLow":987,"adapterLuidHigh":-12}""");
var readOnly = Create();
var reply = readOnly.DisplayInventory();
Check("Display GET routed to NvCapture", reply.Status == 200 && getPaths.Contains("/displays"));
Check("Inventory includes verified worker output", AsJson(reply.Body).GetProperty("displays").GetArrayLength() == 1);
Check("Write disabled by default", readOnly.DisplayCommand(false,chosen).Status == 403 &&
    readOnly.DisplayCommand(true,yes).Status == 403 && postPaths.Count == 0);
var enabled = Create(displayWrite:true,shotWrite:true);
Check("No screenshot without confirmation", enabled.DisplayCommand(true,no).Status == 400);
Check("Missing LUID field rejected", enabled.DisplayCommand(false,JSON("""{"confirmed":true,"outputIndex":2}""")).Status == 400);
Check("Incorrect output index rejected", enabled.DisplayCommand(false,untrusted).Status == 400);
Check("Invalid LUID signedness rejected", enabled.DisplayCommand(false,
    JSON("""{"confirmed":true,"outputIndex":2,"adapterLuidLow":-1,"adapterLuidHigh":-12}""")).Status == 400);
Check("Display selection forwards exactly to NvCapture", enabled.DisplayCommand(false,chosen).Status == 200 &&
    postPaths.Last() == "/display/select");
Check("Display selection re-encodes trusted fields only", JSON(bodies.Last()).EnumerateObject().Count() == 4);
Check("Result preserves rebuild requirement", AsJson(enabled.DisplayCommand(false,chosen).Body)
    .GetProperty("effectiveNow").ValueKind == JsonValueKind.False);
var ss = enabled.DisplayCommand(true,yes);
Check("Screenshot forwards only confirmed request", ss.Status == 200 &&
    postPaths.Last() == "/screenshot" && JSON(bodies.Last()).EnumerateObject().Count() == 1);
Check("Screenshot PascalCase result normalized", AsJson(ss.Body).GetProperty("saved").GetBoolean() &&
    AsJson(ss.Body).GetProperty("file").GetString() == "test.png");
var denied = Create(displayWrite:true,shotWrite:true,testMode:true);
int prior = postPaths.Count;
Check("Test mode blocks display writes", denied.DisplayCommand(false,chosen).Status == 409 &&
    denied.DisplayCommand(true,yes).Status == 409 && prior == postPaths.Count);
var failed = Create(async (req, token) => {
    if (req.Method == HttpMethod.Post)
        return new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(req.RequestUri!.AbsolutePath == "/screenshot" ?
                """{"Saved":false,"ErrorCode":"ffmpeg_binary_missing"}""" :
                """{"saved":false,"error":"capture_busy"}""")};
    return await handler(req,token);
},displayWrite:true,shotWrite:true);
Check("Screenshot worker failure does not report saved", !AsJson(
    failed.DisplayCommand(true,yes).Body).GetProperty("saved").GetBoolean());
Check("Display worker busy does not report saved", !AsJson(
    failed.DisplayCommand(false,chosen).Body).GetProperty("saved").GetBoolean());
var missing = Create((req,_) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
    { Content = new StringContent("{}") }), displayWrite:true,shotWrite:true);
Check("Undeployed display endpoint is Unavailable", missing.DisplayInventory().Status == 501);
Check("Undeployed worker cannot execute command", missing.DisplayCommand(true,yes).Status == 503);
var legacy = Create((req,_) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    { Content = new StringContent(req.RequestUri!.AbsolutePath == "/displays" ?
        """{"error":"not_found"}""" : req.RequestUri!.AbsolutePath == "/health" ? health : state) }),
    displayWrite:true,shotWrite:true);
Check("Legacy 200 not_found mapped to unavailable", legacy.DisplayInventory().Status == 501);
Check("Legacy 200 not_found blocks command", legacy.DisplayCommand(true,yes).Status == 501);
var slow = Create((_,_) => Task.FromException<HttpResponseMessage>(new TaskCanceledException()),
    displayWrite:true,shotWrite:true);
Check("Read timeout maps 504", slow.DisplayInventory().Status == 504);
Check("Health timeout blocks writes", slow.DisplayCommand(true,yes).Status == 503);
var malformed = Create((req,_) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    { Content = new StringContent(req.RequestUri!.AbsolutePath == "/displays" ?
        """{"schemaVersion":1,"displays":[{}]}""" :
        req.RequestUri!.AbsolutePath == "/health" ? health : state) }),
    displayWrite:true,shotWrite:true);
Check("Incomplete display JSON rejected", malformed.DisplayInventory().Status == 503);
Check("Malformed worker inventory denies mutation", malformed.DisplayCommand(false,chosen).Status == 503);
var busy = Create((req,_) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    { Content = new StringContent(req.RequestUri!.AbsolutePath == "/health"
        ? """{"ready":true,"recording":true}""" :
        req.RequestUri!.AbsolutePath == "/state" ? state : displays) }),
    displayWrite:true,shotWrite:true);
Check("Recording worker prevents screenshot", busy.DisplayCommand(true,yes).Status == 409);
Check("Recording worker prevents monitor change", busy.DisplayCommand(false,chosen).Status == 409);
Check("Wrong method cannot invoke screenshot via read", readOnly.DisplayInventory().Status == 200);
Console.WriteLine("SUCCESS "+passed+" isolated display relay tests; no real screenshots or display changes.");

sealed class Fake(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> fn): HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) => fn(req,ct);
}