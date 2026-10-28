using System.Text.Json;

var checks = 0;
void Check(bool pass, string message)
{
    if (!pass) throw new Exception(message);
    checks++;
}
var gate = new HotkeyMessageGate();
for (uint i = 0; i < 20; i++)
{
    Check(gate.TryAccept("OpenShare", 90, 100 + i * 35), "rapid distinct keypress dropped");
    Check(!gate.TryAccept("OpenShare", 90, 100 + i * 35), "same message dispatched twice");
}
gate.Clear();
Check(gate.TryAccept("OpenShare", 90, 100), "editor exit did not reset message identity");
var request = new HotkeyDispatchRequest("http://127.0.0.1/", "{}", 100, 100000, 2);
Check(request.CanDispatch(200, 2, true), "fresh dispatch rejected");
Check(!request.CanDispatch(2101, 2, true), "stale dispatch replayed");
Check(!request.CanDispatch(200, 3, true), "old editor generation replayed");
Check(!request.CanDispatch(200, 2, false), "editor capture was not suppressed");
Check(request.RequestId.Length == 32, "correlation ID missing");

var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["X-OSC-Request-Id"] = "test-press-1", ["X-OSC-Created-At"] = "100000"
};
var published = 0;
JsonElement last = default;
bool Publisher(JsonElement events) { published++; last = events.Clone(); return true; }
var result = HotkeyRequest.Dispatch("OpenShare", "OSC", headers, Publisher, _ => { }, false, 100010);
Check(result.Status == 200 && published == 1, "successful acknowledgement missing");
Check(last[0].GetProperty("data").GetProperty("requestId").GetString() == "test-press-1", "ID not forwarded");
Check(last[0].GetProperty("data").GetProperty("createdAtUnixMs").GetInt64() == 100000, "keypress age lost");
Check(HotkeyRequest.Dispatch("OpenShare", "OSC", headers, _ => false, _ => { }, false, 100010).Status == 503,
    "unavailable Node/OSC falsely reported handled");
Check(HotkeyRequest.Dispatch("OpenShare", "OSC", headers, _ => throw new IOException(), _ => { }, false, 100010).Status == 503,
    "publisher exception falsely reported handled");
Check(HotkeyRequest.Dispatch("OpenShare", "OSC", headers, null, _ => { }, false, 100010).Status == 503,
    "absent publisher falsely reported handled");
Check(HotkeyRequest.Dispatch("OpenShare", "OSC", headers, Publisher, _ => { }, false, 102001).Status == 409,
    "expired HTTP request replayed");
Check(published == 1, "expired request reached publisher");
Check(HotkeyRequest.Dispatch("OpenShare", "OSC", headers, Publisher, _ => { }, false, 98000).Status == 409,
    "future request accepted outside skew tolerance");
headers["X-OSC-Created-At"] = "invalid";
Check(HotkeyRequest.Dispatch("OpenShare", "OSC", headers, Publisher, _ => { }, false, 100010).Status == 409,
    "invalid timestamp reached publisher");

// A delayed downstream result must hold the HTTP acknowledgement; order/timeout
// behavior is exercised by the real ephemeral HTTP server in Node regressions.
headers["X-OSC-Created-At"] = "100000";
using var arrived = new ManualResetEventSlim();
using var release = new ManualResetEventSlim();
var delayed = Task.Run(() => HotkeyRequest.Dispatch("OpenShare", "OSC", headers,
    _ => { arrived.Set(); release.Wait(); return false; }, _ => { }, false, 100010));
Check(arrived.Wait(2000), "mock downstream did not receive intent");
Check(!delayed.IsCompleted, "acknowledgement preceded downstream result");
release.Set();
Check(delayed.GetAwaiter().GetResult().Status == 503, "delayed failure falsely acknowledged");
Console.WriteLine("PASS: " + checks + " hotkey contract assertions (no live services or Win32 registrations)");
