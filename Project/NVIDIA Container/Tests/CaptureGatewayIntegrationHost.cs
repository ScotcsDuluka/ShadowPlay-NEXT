using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

// Offline test host only. Its worker is a test-owned loopback stub.
if (args.Length != 2) throw new ArgumentException("gateway port, stub worker port");
int port = int.Parse(args[0]);
int workerPort = int.Parse(args[1]);
var gateway = new CaptureWorkerGateway(new HttpClient(), workerPort, true, true,
    workerWriteToken: () => "0123456789abcdef0123456789abcdef", requireContainerRecordMode: true);
using var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{port}/");
listener.Start();
Console.WriteLine("READY");
while (true)
{
    var context = await listener.GetContextAsync();
    _ = Task.Run(() => Handle(context));
}

void Handle(HttpListenerContext context)
{
    try
    {
        object body;
        int status = 200;
        string path = context.Request.Url!.AbsolutePath;
        if (path == "/v1/capture/status" && context.Request.HttpMethod == "GET")
            body = gateway.Status();
        else if ((path == "/v1/capture/record/start" || path == "/v1/capture/record/stop") &&
                 context.Request.HttpMethod == "POST")
        {
            using var document = JsonDocument.Parse(context.Request.InputStream);
            var result = gateway.RecordCommand(path.EndsWith("start"), document.RootElement);
            status = result.Status;
            body = result.Body;
        }
        else { status = 404; body = new { error = "not_found" }; }
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body));
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.OutputStream.Write(bytes);
    }
    finally { context.Response.Close(); }
}
