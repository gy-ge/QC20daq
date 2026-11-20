using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QC20daq;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

AppOptions? options = null;
try
{
    options = AppOptions.Parse(args);
    Directory.CreateDirectory(options.Control);
    Directory.CreateDirectory(options.Logs);

    using var lease = LockService(options);
    if (lease is null)
    {
        await OpenExistingAsync(options);
        return 0;
    }

    using (var process = Process.GetCurrentProcess())
        process.PriorityClass = ProcessPriorityClass.BelowNormal;
    using var workbench = new Workbench(options);

    var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
    {
        Args = [],
        ContentRootPath = AppContext.BaseDirectory,
    });
    builder.Logging.ClearProviders();
    builder.WebHost.ConfigureKestrel(server =>
    {
        server.AddServerHeader = false;
        server.Listen(IPAddress.Loopback, options.Port);
        server.Limits.MaxRequestBodySize = 8192;
        server.Limits.MaxConcurrentConnections = 32;
        server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
    });
    await using var app = builder.Build();
    app.Use(async (context, next) =>
    {
        var host = context.Request.Host;
        var headers = context.Response.Headers;
        headers.CacheControl = "no-store";
        headers.XContentTypeOptions = "nosniff";
        headers.ContentSecurityPolicy = "default-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'none'";
        headers["Referrer-Policy"] = "no-referrer";
        if (host.Host is not ("127.0.0.1" or "localhost") || (host.Port ?? 80) != options.Port)
        {
            await WriteError(context, 403, "只允许本机访问。");
            return;
        }

        try { await next(context); }
        catch (ApiError error) when (!context.Response.HasStarted)
        {
            await WriteError(context, error.Status, error.Message);
        }
        catch (JsonException) when (!context.Response.HasStarted)
        {
            await WriteError(context, 400, "请求参数无法读取。");
        }
        catch (BadHttpRequestException error) when (!context.Response.HasStarted)
        {
            await WriteError(context, error.StatusCode, "请求长度或格式异常。");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception error) when (!context.Response.HasStarted && error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            options.Log(error.Message);
            await WriteError(context, 500, error.Message);
        }
    });

    MapAsset(app, "/", "index.html", "text/html; charset=utf-8");
    MapAsset(app, "/app.js", "app.js", "application/javascript; charset=utf-8");
    MapAsset(app, "/app.css", "app.css", "text/css; charset=utf-8");
    app.MapGet("/favicon.ico", () => Results.NoContent());
    app.MapGet("/api/health", () => Results.Json(new
    {
        Application = "QC20daq",
        options.InstanceKey,
        ProcessId = Environment.ProcessId,
    }, Protocol.Json));
    app.MapGet("/api/status", () => Results.Bytes(workbench.Payload, "application/json; charset=utf-8"));
    app.MapPost("/api/connect", (HttpContext context) => ControlAction(context, workbench, body => new { Id = workbench.Connect(body) }));
    app.MapPost("/api/device", (HttpContext context) => ControlAction(context, workbench, body => new { Id = workbench.DeviceOperation(body) }));
    app.MapPost("/api/record", (HttpContext context) => ControlAction(context, workbench, body => new { Id = workbench.StartRecording(body) }));
    app.MapPost("/api/disconnect", (HttpContext context) => ControlAction(context, workbench, body =>
    {
        workbench.Disconnect(body.Text("id"));
        return new { Accepted = true };
    }));
    app.MapPost("/api/stop", (HttpContext context) => ControlAction(context, workbench, body =>
    {
        workbench.Stop(body.Text("id"));
        return new { Accepted = true };
    }));
    app.MapPost("/api/shutdown", (HttpContext context) => ControlAction(context, workbench, _ =>
    {
        workbench.RequestShutdown();
        return new { Accepted = true };
    }));
    app.MapGet("/api/download", (HttpContext context) => SendDownloadAsync(context, workbench));

    await app.StartAsync();
    options.Log("QC20 采集台 " + options.Address);
    using var stopping = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);
    var monitor = workbench.MonitorAsync(stopping.Token, app.Lifetime.StopApplication);
    try
    {
        OpenBrowser(options);
        await app.WaitForShutdownAsync();
    }
    finally
    {
        await stopping.CancelAsync();
        try { await monitor; }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }
    return 0;
}
catch (Exception error)
{
    options?.Log(error.ToString());
    Console.Error.WriteLine(error.Message);
    if (!args.Contains("--no-browser")) DesktopDialog.Show(error.Message);
    return 1;
}

static FileStream? LockService(AppOptions options)
{
    var stream = new FileStream(Path.Combine(options.Control, "service.lock"), FileMode.OpenOrCreate,
        FileAccess.ReadWrite, FileShare.ReadWrite);
    try
    {
        stream.Lock(0, 1);
        return stream;
    }
    catch (IOException)
    {
        stream.Dispose();
        return null;
    }
}

static async Task OpenExistingAsync(AppOptions options)
{
    using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromMilliseconds(350) };
    for (int i = 0; i < 15; i++)
    {
        try
        {
            using var response = await client.GetAsync(options.Address + "/api/health");
            if (response.IsSuccessStatusCode)
            {
                var status = await response.Content.ReadFromJsonAsync<JsonElement>();
                if (((JsonElement?)status).Text("application") == "QC20daq" &&
                    ((JsonElement?)status).Text("instance_key") == options.InstanceKey)
                {
                    OpenBrowser(options);
                    return;
                }
            }
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
        catch (JsonException) { }
        await Task.Delay(120);
    }
    throw new IOException("此运行目录已有 QC20 服务。请检查端口或打开当前服务的网页。");
}

static void OpenBrowser(AppOptions options)
{
    if (!options.OpenBrowser) return;
    try { Process.Start(new ProcessStartInfo(options.Address + "/") { UseShellExecute = true })?.Dispose(); }
    catch (System.ComponentModel.Win32Exception error)
    {
        options.Log(error.Message);
        DesktopDialog.Show($"请在浏览器打开 {options.Address}/");
    }
}

static void MapAsset(WebApplication app, string route, string name, string contentType)
{
    using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("QC20.Web." + name)
        ?? throw new FileNotFoundException($"缺少网页资源 {name}。");
    using var buffer = new MemoryStream();
    resource.CopyTo(buffer);
    var bytes = buffer.ToArray();
    app.MapGet(route, () => Results.Bytes(bytes, contentType));
}

static async Task ControlAction(HttpContext context, Workbench workbench, Func<JsonElement?, object> action)
{
    var request = context.Request;
    string origin = "http://" + request.Host.Value;
    string token = request.Headers["X-QC20-Token"].ToString();
    if (request.Headers.Origin.ToString() != origin || token.Length != workbench.Token.Length ||
        !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.ASCII.GetBytes(workbench.Token)))
        throw new ApiError("本机操作校验已失效，请刷新页面。", 403);

    if (!request.HasJsonContentType()) throw new ApiError("需要 JSON 请求。", 415);
    if (request.ContentLength is < 1 or > 8192) throw new ApiError("请求长度异常。", 413);
    using var body = await JsonDocument.ParseAsync(request.Body, new JsonDocumentOptions { MaxDepth = 16 }, context.RequestAborted);
    if (body.RootElement.ValueKind != JsonValueKind.Object) throw new ApiError("请求参数格式不正确。");
    var result = action(body.RootElement);
    context.Response.StatusCode = 202;
    await context.Response.WriteAsJsonAsync(result, Protocol.Json, context.RequestAborted);
}

static async Task SendDownloadAsync(HttpContext context, Workbench workbench)
{
    var (stream, name) = workbench.OpenDownload(context.Request.Query["id"], context.Request.Query["file"]);
    try
    {
        await using var input = stream;
        bool csv = name.EndsWith(".csv", StringComparison.Ordinal);
        context.Response.ContentType = csv ? "text/csv; charset=utf-8" : "application/json; charset=utf-8";
        context.Response.ContentLength = stream.Length;
        context.Response.Headers.ContentDisposition = $"attachment; filename=\"{name}\"";
        await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
    finally { workbench.DownloadFinished(); }
}

static Task WriteError(HttpContext context, int status, string message)
{
    context.Response.StatusCode = status;
    return context.Response.WriteAsJsonAsync(new { Error = message }, Protocol.Json);
}

internal static class DesktopDialog
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    internal static void Show(string message) => MessageBoxW(IntPtr.Zero, message, "QC20 采集台", 0x30);
}
