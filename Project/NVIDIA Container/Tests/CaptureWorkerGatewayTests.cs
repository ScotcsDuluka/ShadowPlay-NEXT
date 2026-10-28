using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

if (args.Length != 1) throw new Exception("Pass source capture-engines.json path.");
var checks = 0;
void Check(string name, bool condition)
{
    if (!condition) throw new Exception("FAIL " + name);
    Console.WriteLine("PASS " + name);
    checks++;
}
JsonElement ToJson(object value) => JsonSerializer.SerializeToElement(value);

var health = """{"ok":true,"ready":true,"recording":false}""";
var state = """{"ready":true,"recording":false,"actualSessionBackend":"","elapsedSec":0}""";
var engine = JsonSerializer.Serialize(new
{
    schemaVersion = 2, readOnly = true,
    configured = new { backend = "Duluka", captureApi = "dxgi_desktop_duplication",
        encoder = "NVENC_H264", allowed = true },
    actualSessionBackend = "", nativeRuntimeReady = true,
    nativeRuntimeCodec = "NVENC_H264", recording = false,
    ffmpegBinary = new { binaryRunnable = true, captureApis = new[] { "ddagrab" },
        encoders = new[] { "libx264" }, hardwareEncoderVerified = false }
});
int posts = 0;
Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder =
    (req, _) => {
        var path = req.RequestUri!.AbsolutePath;
        if (req.Method == HttpMethod.Post) {
            posts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(path.EndsWith("start") ?
                    """{"started":true,"backend":"Duluka"}""" :
                    """{"stopped":true,"backend":"Duluka"}""") });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(path == "/health" ? health :
                path == "/state" ? state : engine)
        });
    };
CaptureWorkerGateway Make(
    Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> fn,
    bool commands = false, bool testMode = false) =>
    new(new HttpClient(new Stub(fn)), 59599, true, commands, testMode,
        workerWriteToken: () => "0123456789abcdef0123456789abcdef");

var good = Make(responder);
var status = ToJson(good.Status());
Check("Ready GET health and state", status.GetProperty("availability").GetString() == "Available");
Check("Idle state is not simulated recording", status.GetProperty("recording").ValueKind == JsonValueKind.False);
Check("Replay not invented", !status.GetProperty("replay").GetProperty("commandSupported").GetBoolean());
var catalog = ToJson(good.Engines(args[0]));
Check("Engine v2 accepted", catalog.GetProperty("workerTruthAvailable").GetBoolean());
Check("Duluka runtime backed by worker", catalog.GetProperty("providers")[0].GetProperty("runtimeReady").GetBoolean());
Check("FFmpeg hardware not inferred", !catalog.GetProperty("providers")[1].GetProperty("runtimeReady").GetBoolean());
Check("Unverified GPU encoders excluded", catalog.GetProperty("providers")[2].GetProperty("encoders").GetArrayLength() == 0);
var capabilities = ToJson(good.Capabilities(args[0]));
Check("Capabilities mirror worker inventory", capabilities.GetProperty("availability").GetString() == "Available");

var missing = Make((req, _) => Task.FromResult(new HttpResponseMessage(
    req.RequestUri!.AbsolutePath == "/engines" ? HttpStatusCode.NotFound : HttpStatusCode.OK) {
        Content = new StringContent(req.RequestUri.AbsolutePath == "/health" ? health : state)
    }));
var gone = ToJson(missing.Engines(args[0]));
Check("404 engines yields Unavailable", !gone.GetProperty("workerTruthAvailable").GetBoolean());
Check("404 preserves reason", gone.GetProperty("workerError").GetString() == "endpoint_not_found");
Check("404 cannot invent encoder", gone.GetProperty("providers")[1].GetProperty("encoders").GetArrayLength() == 0);
var legacy = Make((req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
    Content = new StringContent(req.RequestUri!.AbsolutePath == "/engines" ?
        """{"error":"not_found"}""" : req.RequestUri.AbsolutePath == "/health" ? health : state)
}));
Check("Legacy 200 not_found mapped to 404", legacy.Read("engines").Code == "endpoint_not_found");

var timeout = Make((_, _) => Task.FromException<HttpResponseMessage>(new TaskCanceledException()));
Check("Timeout distinguished from 404", timeout.Read("engines").Code == "timeout");
Check("Timeout means unknown recording", ToJson(timeout.Status()).GetProperty("recording").ValueKind ==
    JsonValueKind.Null);
var malformed = Make((req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
    Content = new StringContent(req.RequestUri!.AbsolutePath == "/engines" ?
        """{"schemaVersion":2,"readOnly":true}""" :
        req.RequestUri.AbsolutePath == "/health" ? health : state)
}));
Check("Incomplete JSON is rejected", malformed.Read("engines").Code == "invalid_response");
Check("Incomplete JSON never ready", !ToJson(malformed.Engines(args[0]))
    .GetProperty("workerTruthAvailable").GetBoolean());

var confirm = JsonDocument.Parse("""{"confirmed":true}""").RootElement.Clone();
var noConfirm = JsonDocument.Parse("""{"confirmed":false}""").RootElement.Clone();
var denied = good.RecordCommand(true, confirm);
Check("Commands default denied", denied.Status == 403 && posts == 0);
var enabled = Make(responder, commands:true);
Check("Explicit confirmation required", enabled.RecordCommand(true, noConfirm).Status == 400 && posts == 0);
Check("Confirmed start forwarded only to mock", enabled.RecordCommand(true, confirm).Status == 200 && posts == 1);
int authorizationPosts = posts;
var noWorkerToken = new CaptureWorkerGateway(new HttpClient(new Stub(responder)),
    59599, true, true);
Check("Missing worker authorization never sends POST", noWorkerToken.RecordCommand(true, confirm).Status == 503 && posts == authorizationPosts);
var invalidWorkerToken = new CaptureWorkerGateway(new HttpClient(new Stub(responder)),
    59599, true, true, workerWriteToken: () => "invalid\r\nheader");
Check("Invalid worker authorization never sends POST", invalidWorkerToken.RecordCommand(true, confirm).Status == 503 && posts == authorizationPosts);
var unreadableWorkerToken = new CaptureWorkerGateway(new HttpClient(new Stub(responder)),
    59599, true, true, workerWriteToken: () => throw new IOException("not exposed"));
Check("Unreadable protected enrollment fails closed", unreadableWorkerToken.RecordCommand(true, confirm).Status == 503 && posts == authorizationPosts);
bool writeHeaderPresent = false;
bool readHeaderAbsent = true;
var authenticatedWorker = Make((req, token) => {
    if (req.Method == HttpMethod.Post)
        writeHeaderPresent = req.Headers.TryGetValues("X-ShadowPlay-Capture-Token", out var values) &&
            values.Single() == "0123456789abcdef0123456789abcdef";
    else readHeaderAbsent &= !req.Headers.Contains("X-ShadowPlay-Capture-Token");
    return responder(req, token);
}, commands:true);
authenticatedWorker.RecordCommand(true, confirm);
Check("Protected worker credential attached only to mutation", writeHeaderPresent && readHeaderAbsent);
posts = authorizationPosts;
Check("Test mode blocks commands", Make(responder, commands:true, testMode:true)
    .RecordCommand(true, confirm).Status == 409 && posts == 1);
Check("Stopping idle worker rejected", enabled.RecordCommand(false, confirm).Status == 409 && posts == 1);
var recordingState = state.Replace("\"recording\":false", "\"recording\":true")
    .Replace("\"actualSessionBackend\":\"\"", "\"actualSessionBackend\":\"Duluka\"");
var recording = Make((req, token) => {
    if (req.RequestUri!.AbsolutePath == "/state" &&
        req.Method == HttpMethod.Get) return Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) {Content = new StringContent(recordingState)});
    return responder(req, token);
}, commands:true);
Check("Active record stop forwarded", recording.RecordCommand(false, confirm).Status == 200 && posts == 2);
int postsBeforePreparingStop = posts;
var preparingState = recordingState.Replace("\"actualSessionBackend\":\"Duluka\"",
    "\"actualSessionBackend\":\"\",\"runtimeTruth\":{\"lifecycleOwner\":\"Duluka\",\"actualRunningEngine\":\"\"}");
var preparing = Make((req, token) => req.Method == HttpMethod.Get &&
    req.RequestUri!.AbsolutePath == "/state"
        ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content = new StringContent(preparingState)})
        : responder(req, token), commands:true);
Check("Preparing session can be cancelled without inventing actual running engine",
    preparing.RecordCommand(false, confirm).Status == 200 && posts == postsBeforePreparingStop + 1);
posts = postsBeforePreparingStop;
var partialStop = Make((req, token) => {
    if (req.Method == HttpMethod.Post)
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content = new StringContent(
            """{"stopped":true,"saved":false,"finalizationState":"Partial","mediaVerified":false,"error":"recording_not_verified_saved"}""")});
    if (req.RequestUri!.AbsolutePath == "/state")
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content = new StringContent(recordingState)});
    return responder(req, token);
}, commands:true);
var partialResult = ToJson(partialStop.RecordCommand(false, confirm).Body);
Check("Lifecycle stop does not turn Partial into Saved",
    partialResult.GetProperty("accepted").GetBoolean() &&
    !partialResult.GetProperty("saved").GetBoolean() &&
    partialResult.GetProperty("finalizationState").GetString() == "Partial" &&
    partialResult.GetProperty("error").GetString() == "recording_not_verified_saved");
var pending = Make((req, token) => {
    if (req.Method == HttpMethod.Post)
        return Task.FromException<HttpResponseMessage>(new TaskCanceledException());
    return responder(req, token);
}, commands:true);
var pendingReply = pending.RecordCommand(true, confirm);
Check("POST timeout is unknown outcome",
    pendingReply.Status == 504 &&
    ToJson(pendingReply.Body).GetProperty("outcomeUnknown").GetBoolean());
Check("Ambiguous transport timeout blocks blind retries",
    pending.RecordCommand(true, confirm).Status == 409 &&
    ToJson(pending.Status()).GetProperty("startOutcomeUnresolved").GetBoolean());
var workerPending = Make((req, token) => {
    if (req.Method == HttpMethod.Post)
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(
                """{"started":false,"error":"start_status_pending_timeout"}""")
        });
    return responder(req, token);
}, commands:true);
var wp = workerPending.RecordCommand(true, confirm);
Check("Worker pending timeout also means unknown outcome",
    wp.Status == 504 && ToJson(wp.Body).GetProperty("outcomeUnknown").GetBoolean());
Check("Worker pending response locks out repeated start",
    workerPending.RecordCommand(true, confirm).Status == 409);
var stopEvenWithoutEngines = Make((req, token) => {
    var path = req.RequestUri!.AbsolutePath;
    if (path == "/state" && req.Method == HttpMethod.Get)
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(recordingState.Replace("\"ready\":true", "\"ready\":false"))
        });
    if (path == "/engines") return Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.NotFound));
    if (path == "/health") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
        Content = new StringContent("""{"ready":false,"recording":true}""")
    });
    return responder(req, token);
}, commands:true);
Check("Stop remains possible without engines while state proves active session",
    stopEvenWithoutEngines.RecordCommand(false, confirm).Status == 200 && posts == 3);
Check("Missing policy is independently unavailable",
    !ToJson(good.Engines(args[0] + ".missing")).GetProperty("policyValid").GetBoolean());
const string legacyIdleState =
    """{"ready":true,"failReason":"","recording":false,"enabled":false,"elapsedSec":0,"outputPath":"","lastFile":""}""";
var oldWorker = Make((req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
    Content = new StringContent(req.RequestUri!.AbsolutePath == "/state"
        ? legacyIdleState : req.RequestUri.AbsolutePath == "/health" ? health : """{"error":"not_found"}""")
}), commands:true);
Check("Deployed legacy idle state is an explicitly recognized contract",
    oldWorker.Read("state").Success);
var oldStatus = ToJson(oldWorker.Status());
Check("Legacy NvCapture available rather than false Unavailable",
    oldStatus.GetProperty("availability").GetString() == "Available");
Check("Legacy worker healthy even without engine catalog",
    oldStatus.GetProperty("workerReady").GetBoolean());
Check("Legacy worker health cannot imply engine runtime readiness",
    !oldStatus.GetProperty("runtimeReady").GetBoolean() &&
    !oldStatus.GetProperty("engineEvidenceAvailable").GetBoolean());
Check("Legacy idle recording state remains truthful",
    oldStatus.GetProperty("recording").ValueKind == JsonValueKind.False);
Check("Legacy owner is unknown, never guessed",
    oldStatus.GetProperty("actualSessionBackend").ValueKind == JsonValueKind.Null);
Check("Legacy diagnostic contract labeled explicitly",
    oldStatus.GetProperty("stateContract").GetString() == "legacy");
Check("Current worker still recognized as current contract",
    ToJson(good.Status()).GetProperty("stateContract").GetString() == "current");
Check("Current native worker readiness requires explicit native evidence",
    ToJson(good.Status()).GetProperty("workerReady").GetBoolean() &&
    ToJson(good.Status()).GetProperty("runtimeReady").GetBoolean() &&
    ToJson(good.Status()).GetProperty("engineEvidenceAvailable").GetBoolean());
var ffmpegWithoutHardware = Make((req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
    Content = new StringContent(req.RequestUri!.AbsolutePath == "/engines"
        ? engine.Replace("\"Duluka\"", "\"FFmpeg\"")
            .Replace("\"nativeRuntimeReady\":true", "\"nativeRuntimeReady\":false")
        : req.RequestUri.AbsolutePath == "/state" ? state : health)
}));
Check("FFmpeg binary inventory alone cannot claim engine runtime ready",
    ToJson(ffmpegWithoutHardware.Status()).GetProperty("workerReady").GetBoolean() &&
    !ToJson(ffmpegWithoutHardware.Status()).GetProperty("runtimeReady").GetBoolean());
Check("Legacy worker /engines remains independently unavailable",
    !ToJson(oldWorker.Engines(args[0])).GetProperty("workerTruthAvailable").GetBoolean());
var legacyRecording = Make((req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
    Content = new StringContent(req.RequestUri!.AbsolutePath == "/state"
        ? legacyIdleState.Replace("\"recording\":false", "\"recording\":true")
        : req.RequestUri.AbsolutePath == "/health" ? health : """{"error":"not_found"}""")
}), commands:true);
Check("Legacy active worker recording is reported but backend stays unknown",
    ToJson(legacyRecording.Status()).GetProperty("recording").GetBoolean() &&
    ToJson(legacyRecording.Status()).GetProperty("actualSessionBackend").ValueKind == JsonValueKind.Null);
Check("Unknown active backend cannot receive unsafe stop",
    legacyRecording.RecordCommand(false, confirm).Status == 409);
var incompleteLegacyState = Make((req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
    Content = new StringContent(req.RequestUri!.AbsolutePath == "/state" ?
        """{"ready":true,"recording":false,"elapsedSec":0,"enabled":false}""" : health)
}));
Check("Partial legacy JSON fails closed",
    incompleteLegacyState.Read("state").Code == "invalid_response");

const string displaysV1 = """
{"schemaVersion":1,"readOnly":true,"source":"NvCapture",
"displays":[{"OutputIndex":0,"AdapterLuidLow":1234,"AdapterLuidHigh":0}],
"selected":{"OutputIndex":0,"AdapterLuidLow":1234,"AdapterLuidHigh":0},
"selectionValid":true,"error":"","recording":false,"appliesOnEngineRebuild":true}
""";
int readonlyGets = 0;
int readonlyPosts = 0;
var negotiatedWorker = Make((req, _) => {
    if (req.Method == HttpMethod.Post) readonlyPosts++; else readonlyGets++;
    var content = req.RequestUri!.AbsolutePath switch {
        "/health" => health, "/state" => state, "/engines" => engine,
        "/displays" => displaysV1, _ => """{"error":"not_found"}"""
    };
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
        Content = new StringContent(content)
    });
});
var contract = ToJson(negotiatedWorker.Contract());
Check("Negotiation reads verified real engine v2 and display v1",
    contract.GetProperty("workerApiVersion").GetInt32() == 2 &&
    contract.GetProperty("displayApiVersion").GetInt32() == 1 &&
    contract.GetProperty("endpoints").GetProperty("engines").GetProperty("schemaVerified").GetBoolean() &&
    contract.GetProperty("endpoints").GetProperty("displays").GetProperty("schemaVerified").GetBoolean());
Check("Negotiation does not POST or change display/screenshot",
    readonlyPosts == 0 && readonlyGets == 4);
Check("Unversioned health and state do not invent schema version",
    contract.GetProperty("endpoints").GetProperty("health").GetProperty("schemaVersion").ValueKind ==
        JsonValueKind.Null &&
    contract.GetProperty("endpoints").GetProperty("state").GetProperty("schemaVersion").ValueKind ==
        JsonValueKind.Null);
Check("A readable display list does not verify screenshot command",
    !contract.GetProperty("commands").GetProperty("screenshot").GetProperty("workerCommandVerified").GetBoolean() &&
    !contract.GetProperty("commands").GetProperty("screenshot").GetProperty("runtimeReady").GetBoolean());
Check("Write routes remain disabled during read-only negotiation",
    !contract.GetProperty("commands").GetProperty("record").GetProperty("gatewayEnabled").GetBoolean() &&
    !contract.GetProperty("commands").GetProperty("displaySelect").GetProperty("gatewayEnabled").GetBoolean() &&
    !contract.GetProperty("commands").GetProperty("screenshot").GetProperty("gatewayEnabled").GetBoolean());
var oldContract = ToJson(oldWorker.Contract());
Check("Legacy worker negotiation never invents endpoint versions",
    oldContract.GetProperty("workerApiVersion").ValueKind == JsonValueKind.Null &&
    oldContract.GetProperty("displayApiVersion").ValueKind == JsonValueKind.Null &&
    oldContract.GetProperty("endpoints").GetProperty("engines").GetProperty("code").GetString() ==
        "endpoint_not_found");
Check("Legacy state can remain Available while capabilities are Unavailable",
    oldContract.GetProperty("availability").GetString() == "Available" &&
    oldContract.GetProperty("stateContract").GetString() == "legacy" &&
    !oldContract.GetProperty("endpoints").GetProperty("displays").GetProperty("schemaVerified").GetBoolean());
TimeSpan fakeUptime = TimeSpan.FromSeconds(2);
bool online = false;
var warmup = new CaptureWorkerGateway(new HttpClient(new Stub((req, _) =>
    online ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
        Content = new StringContent(req.RequestUri!.AbsolutePath == "/health" ? health
            : req.RequestUri.AbsolutePath == "/state" ? state : engine)
    }) : Task.FromException<HttpResponseMessage>(new HttpRequestException("stub offline")))),
    59599, true, false, elapsedSinceStartup: () => fakeUptime);
var coldStatus = ToJson(warmup.Status());
Check("Cold worker reports Starting, never false Ready",
    coldStatus.GetProperty("startupPhase").GetString() == "Starting" &&
    !coldStatus.GetProperty("workerReady").GetBoolean() &&
    coldStatus.GetProperty("recording").ValueKind == JsonValueKind.Null);
fakeUptime = TimeSpan.FromSeconds(35);
Check("Unrecovered startup transitions to Unavailable after grace",
    ToJson(warmup.Status()).GetProperty("startupPhase").GetString() == "Unavailable");
online = true;
Check("Recovered worker clears startup grace and becomes Ready",
    ToJson(warmup.Status()).GetProperty("startupPhase").GetString() == "Ready");
online = false;
fakeUptime = TimeSpan.FromSeconds(40);
Check("Worker outage after prior success is Degraded, not Starting",
    ToJson(warmup.Status()).GetProperty("startupPhase").GetString() == "Degraded");
Console.WriteLine("SUCCESS: " + checks + " Capture Worker gateway tests (stub HTTP only).");

sealed class Stub(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> handler)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken) => handler(request, cancellationToken);
}
