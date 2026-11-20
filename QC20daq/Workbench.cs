using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QC20daq;

internal sealed record RecordRequest(string Id, long Token, double? Seconds);

internal sealed class Connection(WorkerJob job)
{
    public string Id { get; } = job.Id;
    public string Source { get; } = job.Source!;
    public string? Target { get; } = job.Target;
    public object Rate { get; } = job.RequestedRate == "native"
        ? "native" : double.Parse(job.RequestedRate!, CultureInfo.InvariantCulture);
    public bool Ready { get; set; }
    public double? RateHz { get; set; }
    public JsonElement? Device { get; set; }
}

internal sealed class Capture(RecordRequest request, string source, string folder)
{
    public string Id { get; } = request.Id;
    public string Source { get; } = source;
    public string Folder { get; } = folder;
    public double? TargetSeconds { get; } = request.Seconds;
    public long SamplesWritten { get; set; }
    public double? ElapsedS { get; set; }
    public JsonElement? Report { get; set; }
    public string State { get; set; } = "arming";
}

internal sealed record HistoryItem(string Id, string Source, string? State, long Samples, double? RateHz,
    double? Seconds, string? ClosedUtc, bool HasCsv);

internal sealed class Workbench : IDisposable
{
    private static readonly string[] Sources = ["device", "simulated"];
    private static readonly Regex SessionName = new(@"\Aweb_[0-9]{8}_[0-9]{6}\z");
    private static readonly Regex LiveName = new(@"\Alive_[a-f0-9]{24}\z");
    private static readonly Regex ShortId = new(@"\A[a-f0-9]{24}\z");
    private static readonly Regex SerialName = new(@"\A[a-zA-Z0-9][a-zA-Z0-9_-]{0,31}\z");
    private static readonly JsonElement EmptyDevices = JsonSerializer.SerializeToElement(Array.Empty<TraceDevice>());

    private readonly object _locked = new();
    private readonly AppOptions _options;
    private readonly CollectorProcess _collector;
    private readonly TraceSelection? _trace;
    private WorkerJob? _job;
    private Process? _worker;
    private bool _busy;
    private bool _shuttingDown;
    private string _phase = "idle";
    private string _message = "选择设备并连接";
    private Connection? _connection;
    private Capture? _capture;
    private RecordRequest? _request;
    private string? _recordMessage;
    private LivePreview? _preview;
    private (Connection Connection, Preview Plot)? _interruptedPreview;
    private List<HistoryItem> _history = [];
    private JsonElement? _devices;
    private JsonElement? _lastDevice;
    private int _downloads;
    private byte[] _payload = [];

    internal string Token { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
    internal byte[] Payload => Volatile.Read(ref _payload);
    private string Control => Path.Combine(_options.Control, _job!.Id);

    internal Workbench(AppOptions options)
    {
        _options = options;
        _collector = new(options);
        Directory.CreateDirectory(options.Control);
        Directory.CreateDirectory(options.Logs);
        Directory.CreateDirectory(Path.Combine(options.Rotary, "temp"));
        foreach (string source in Sources) Directory.CreateDirectory(Path.Combine(options.Captures, source));
        _trace = TraceSelection.Read();
        RefreshHistory();
        Restore();
        Tick();
    }

    private void Restore()
    {
        if (Protocol.Read(_collector.Journal) is not { ValueKind: JsonValueKind.Object } saved) return;
        WorkerJob? job;
        try { job = saved.Deserialize<WorkerJob>(Protocol.Json); }
        catch (JsonException) { return; }
        if (job?.Id is null || job.Pid <= 0 || job.Identity <= 0) return;
        if (job.Kind == "live")
        {
            if (!LiveName.IsMatch(job.Id) || !Sources.Contains(job.Source) || job.RequestedRate is null) return;
        }
        else if (job.Kind is not ("discover" or "info") || !ShortId.IsMatch(job.Id)) return;

        _job = job;
        _worker = CollectorProcess.Resume(job);
        _busy = true;
        if (job.Kind == "live")
        {
            _connection = new(job);
            if (Protocol.Read(Path.Combine(Control, "record.json")) is { ValueKind: JsonValueKind.Object } request)
            {
                try
                {
                    var record = request.Deserialize<RecordRequest>(Protocol.Json);
                    if (record?.Id is { } id && SessionName.IsMatch(id) && record.Token > 0)
                        ShowRecording(record);
                }
                catch (JsonException) { _options.Log("现有记录请求无法识别。"); }
            }
        }
    }

    internal string Connect(JsonElement? body)
    {
        var source = body.Text("source");
        if (!Sources.Contains(source)) throw new ApiError("请选择实机或模拟采集。");
        string rate;
        if (body.Text("rate") == "native") rate = "native";
        else if (body.Number("rate") is double hz && hz > 0 && hz <= 1000) rate = hz.ToString("R", CultureInfo.InvariantCulture);
        else throw new ApiError("采样率须为设备原生或不高于 1000 Hz 的正数。");
        var target = body.Text("target");
        var endpoint = source == "simulated" ? [] : TargetArgs(target);
        lock (_locked)
        {
            RequireIdle();
            string id = "live_" + RandomId();
            var job = new WorkerJob(id, "live", source, source == "device" ? target : null, rate);
            string control = Path.Combine(_options.Control, id);
            Directory.CreateDirectory(control);
            _collector.WriteProvenance(control);
            string command = source == "device" ? "live" : "live-simulate";
            Launch(job, [command, control, Path.Combine(_options.Captures, source!), rate, .. endpoint]);
            _connection = new(job);
            ClearPreview();
            _interruptedPreview = null;
            _capture = null;
            _request = null;
            _recordMessage = null;
            _phase = "connecting";
            _message = "正在连接球杆";
            Publish();
            return id;
        }
    }

    internal string DeviceOperation(JsonElement? body)
    {
        string? operation = body.Text("operation");
        if (operation is not ("info" or "discover")) throw new ApiError("未知设备操作。");
        var args = operation == "info" ? new[] { operation }.Concat(TargetArgs(body.Text("target"))).ToArray()
            : [operation];
        lock (_locked)
        {
            RequireIdle();
            var job = new WorkerJob(RandomId(), operation, null, null, null);
            Launch(job, args);
            UpdateDevice(active: true);
            Publish();
            return job.Id;
        }
    }

    internal void Disconnect(string? id)
    {
        lock (_locked)
        {
            RequireRunning();
            if (_connection is null || _connection.Id != id)
                throw new ApiError("该连接已结束，或不是当前设备。", 409);
            Protocol.Signal(Path.Combine(Control, "disconnect.requested"));
            _phase = "disconnecting";
            _message = _request is null ? "正在断开" : "正在保存并断开";
            Publish();
        }
    }

    internal void RequestShutdown()
    {
        lock (_locked)
        {
            if (_shuttingDown) return;
            if (_connection is not null)
                Protocol.Signal(Path.Combine(Control, "disconnect.requested"));
            _shuttingDown = true;
            Publish();
        }
    }

    internal string StartRecording(JsonElement? body)
    {
        var duration = body.Get("seconds");
        double? seconds = body.Number("seconds");
        if (duration.ValueKind != JsonValueKind.Null && !(seconds is > 0))
            throw new ApiError("定时模式必须设置正数时长。");

        lock (_locked)
        {
            RequireRunning();
            if (_connection is null || body.Text("connection_id") != _connection.Id || _phase != "previewing" ||
                _preview?.Snapshot is not { Count: > 0, Stale: false })
                throw new ApiError("请等待球杆实时读数后开始记录。", 409);
            if (_request is not null || _downloads > 0) throw new ApiError("当前操作尚未结束。", 409);
            string time = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string id = $"web_{time}";
            Directory.CreateDirectory(Session(_connection.Source, id));
            long token = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8)) & ((1L << 62) - 1);
            var request = new RecordRequest(id, token + 1, seconds);
            Protocol.Write(Path.Combine(Control, "record.json"), request);
            ShowRecording(request);
            _phase = "arming";
            _message = "正在准备保存";
            Publish();
            return id;
        }
    }

    internal void Stop(string? id)
    {
        lock (_locked)
        {
            RequireRunning();
            if (_capture is null || _request is null || _capture.Id != id)
                throw new ApiError("该记录已结束，或不是当前会话。", 409);
            Protocol.Signal(Path.Combine(_capture.Folder, "stop.requested"));
            _capture.State = "stopping";
            if (_phase != "disconnecting")
            {
                _phase = "stopping";
                _message = "正在保存记录";
            }
            Publish();
        }
    }

    private static string RandomId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));

    private string[] TargetArgs(string? target)
    {
        if (target == "trace")
        {
            if (_trace is null) throw new ApiError("Trace 历史设备不唯一，请搜索后按序列号选择。");
            return [_trace.Device.BluetoothMode, _trace.Address, _trace.Service.ToString(), _trace.Device.Serial];
        }
        if (target is not null && SerialName.IsMatch(target)) return ["serial", target];
        throw new ApiError("请选择 Trace 已存设备，或使用搜索结果中的序列号。");
    }

    private void Launch(WorkerJob job, string[] args)
    {
        (_job, _worker) = _collector.Launch(job, args);
        _busy = true;
    }

    private void RequireIdle()
    {
        RequireRunning();
        if (_busy || _downloads > 0) throw new ApiError("当前操作尚未结束。", 409);
    }

    private void RequireRunning()
    {
        if (_shuttingDown) throw new ApiError("本机服务正在关闭。", 409);
    }

    private string Session(string source, string id)
    {
        if (!Sources.Contains(source) || !SessionName.IsMatch(id)) throw new ApiError("采集会话无效。");
        string path = Path.Combine(_options.Captures, source, id);
        if (Directory.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new ApiError("采集目录须为本机工作区中的实际目录。");
        return path;
    }

    private static string CsvName(string id) => $"ballbar_{id[4..]}.csv";

    private void ShowRecording(RecordRequest request)
    {
        _request = request;
        string source = _connection!.Source;
        _capture = new(request, source, Session(source, request.Id));
        _recordMessage = null;
    }

    private void RefreshHistory()
    {
        var paths = Sources.SelectMany(source => Directory.EnumerateDirectories(Path.Combine(_options.Captures, source), "web_*")
            .Select(path => (Source: source, Id: Path.GetFileName(path), Path: path)))
            .Where(entry => SessionName.IsMatch(entry.Id)).OrderByDescending(entry => entry.Id, StringComparer.Ordinal);
        var latest = new List<HistoryItem>();
        foreach (var entry in paths)
        {
            if (File.GetAttributes(entry.Path).HasFlag(FileAttributes.ReparsePoint)) continue;
            var result = Protocol.Read(Path.Combine(entry.Path, "capture.json"));
            if (result.Text("source") != entry.Source) continue;
            latest.Add(new(entry.Id, entry.Source, result.Text("state"), result.Integer("samples_written"),
                result.Number("nominal_sample_rate_hz"), result.Number("nominal_sample_span_s"), result.Text("closed_utc"),
                File.Exists(Path.Combine(entry.Path, CsvName(entry.Id)))));
            if (latest.Count == 12) break;
        }
        _history = latest;
    }

    private void UpdateLive(bool active)
    {
        var connection = _connection!;
        if (active && _preview is null)
        {
            var ready = Protocol.Read(Path.Combine(Control, "ready.json"));
            if (ready.Text("preview_map") is string mapping && ready.Number("nominal_sample_rate_hz") is double rate)
            {
                _preview = new LivePreview(mapping, rate);
                var device = ready.Get("device");
                connection.Device = device.ValueKind == JsonValueKind.Object ? device : null;
                connection.RateHz = rate;
            }
        }
        _preview?.Update();
        connection.Ready = _preview?.HasContinuousReadings == true;

        if (_request is not null && _capture is not null)
            UpdateRecording(_request, _capture, active);

        if (!active)
        {
            var ended = Protocol.Read(Path.Combine(Control, "ended.json"));
            bool closed = ended.Text("state") == "closed";
            if (!closed && _preview?.Snapshot is { Count: > 0 } plot)
                _interruptedPreview = (connection, plot with { Stale = true });
            _connection = null;
            ClearPreview();
            if (closed)
            {
                _phase = "idle";
                _message = _recordMessage is null ? "设备已断开" : "设备已断开 · " + _recordMessage;
            }
            else
            {
                _phase = "interrupted";
                _message = LiveFailureMessage(ended);
            }
        }
        else if (File.Exists(Path.Combine(Control, "disconnect.requested")))
        {
            _phase = "disconnecting";
            _message = _request is null ? "正在断开" : "正在保存并断开";
        }
        else if (_request is not null)
        {
            _phase = _capture!.State;
            _message = _phase switch
            {
                "recording" => "正在记录并写入 CSV",
                "stopping" => "正在保存记录",
                _ => "正在准备保存",
            };
        }
        else if (_preview?.Snapshot.Stale == true)
        {
            _phase = "preview_stalled";
            _message = "暂未收到新样本，等待读数恢复";
        }
        else if (connection.Ready)
        {
            _phase = "previewing";
            _message = _recordMessage ?? "设备已连接 · 实时预览中";
        }
        else
        {
            _phase = "connecting";
            _message = _preview is null ? "正在连接球杆" : "正在确认连续读数";
        }
    }

    private string LiveFailureMessage(JsonElement? ended)
    {
        var errors = ended.Get("errors");
        string error = errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0
            ? errors[0].ToString() : JobError("采集进程已退出");
        return error switch
        {
            "TimeoutException: No ready QC20 connection within 30 seconds." => "30 秒内未能连接球杆",
            "IOException: no_recent_readings" => "连续未收到新样本，球杆连接已中断",
            "IOException: transport_error: Failed to verify message" => "球杆通信帧校验失败，连接已中断",
            "IOException: device_stopped_unexpectedly" => "球杆提前停止发送样本，连接已中断",
            _ => error,
        };
    }

    private void UpdateRecording(RecordRequest request, Capture capture, bool active)
    {
        var report = Protocol.Read(Path.Combine(capture.Folder, "capture.json"));
        if (report is { ValueKind: JsonValueKind.Object })
        {
            capture.Report = report;
            capture.State = report.Text("state") ?? "interrupted";
            capture.SamplesWritten = report.Integer("samples_written");
            capture.ElapsedS = report.Number("nominal_sample_span_s");
            var error = report.Get("errors");
            _recordMessage = capture.State == "complete"
                ? report.Text("stop_reason") == "duration_reached" ? "定时记录已保存" : "记录已保存"
                : error.ValueKind == JsonValueKind.Array && error.GetArrayLength() > 0
                    ? error[0].ToString()
                    : report.Text("stop_reason") == "stopped_before_recording" ? "开录前已停止"
                    : "记录中断 · " + report.Text("stop_reason");
        }
        else if (!active)
        {
            capture.State = "interrupted";
            _recordMessage = "采集进程已退出，缺少结束报告";
        }
        else
        {
            if (_preview?.RecordToken == request.Token)
            {
                capture.State = "recording";
                capture.SamplesWritten = _preview.Written;
                capture.ElapsedS = _preview.Written > 0 ? (_preview.Written - 1) / _connection!.RateHz : null;
            }
            if (File.Exists(Path.Combine(capture.Folder, "stop.requested"))) capture.State = "stopping";
            return;
        }

        _request = null;
        RefreshHistory();
    }

    private string JobError(string fallback)
    {
        var message = _collector.Tail(_job!.Id).LastOrDefault();
        return string.IsNullOrEmpty(message) ? fallback : message[..Math.Min(500, message.Length)];
    }

    private void UpdateDevice(bool active)
    {
        bool discover = _job!.Kind == "discover";
        if (active)
        {
            _phase = discover ? "discovering" : "reading";
            _message = discover ? "正在搜索附近球杆" : "正在读取球杆信息";
            return;
        }
        var result = _collector.LastResult(_job.Id, discover ? JsonValueKind.Array : JsonValueKind.Object);
        if (discover && result is not null)
        {
            _devices = result;
            _phase = "idle";
            _message = $"搜索完成，找到 {result.Value.GetArrayLength()} 个设备";
        }
        else if (!discover && result.Number("native_sample_rate_hz") is not null)
        {
            _lastDevice = result;
            _phase = "idle";
            _message = "设备信息已读取，请连接设备";
        }
        else
        {
            _phase = "interrupted";
            _message = JobError("设备操作未返回结果");
        }
    }

    private void Publish()
    {
        var snapshot = new
        {
            Token, Phase = _shuttingDown ? "shutting_down" : _phase, Busy = _busy,
            ShuttingDown = _shuttingDown, Message = _shuttingDown ? ShutdownMessage() : _message,
            JobId = _job?.Id, Activity = _job?.Kind, TraceDevice = _trace?.Device,
            Devices = _devices ?? EmptyDevices,
            LastDevice = _lastDevice, Connection = _connection, RecordingActive = _request is not null,
            Capture = _capture, InterruptedConnection = _interruptedPreview?.Connection,
            Preview = _preview?.Snapshot ?? _interruptedPreview?.Plot ?? Preview.Empty, History = _history,
        };
        Volatile.Write(ref _payload, JsonSerializer.SerializeToUtf8Bytes(snapshot, Protocol.Json));
    }

    private string ShutdownMessage()
    {
        if (_request is not null) return "正在保存记录，保存结束后关闭服务";
        if (_connection is not null) return "正在释放球杆并关闭服务";
        if (_busy) return "设备操作结束后关闭服务";
        if (_downloads > 0) return "当前下载完成后关闭服务";
        return "正在关闭本机服务";
    }

    private bool Tick()
    {
        lock (_locked)
        {
            if (_busy)
            {
                bool active = _worker is { HasExited: false };
                if (_job!.Kind == "live") UpdateLive(active);
                else UpdateDevice(active);
                if (!active)
                {
                    _busy = false;
                    _worker?.Dispose();
                    _worker = null;
                }
            }
            Publish();
            return _shuttingDown && !_busy && _downloads == 0;
        }
    }

    internal async Task MonitorAsync(CancellationToken stopping, Action stopApplication)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(60));
        long lastError = 0;
        while (await timer.WaitForNextTickAsync(stopping))
        {
            try
            {
                if (Tick())
                {
                    _options.Log("网页请求关闭，设备与文件操作已结束。");
                    stopApplication();
                    return;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ApiError or JsonException)
            {
                if (lastError == 0 || Stopwatch.GetElapsedTime(lastError).TotalSeconds >= 5)
                {
                    _options.Log(error.Message);
                    lastError = Stopwatch.GetTimestamp();
                }
            }
        }
    }

    internal (FileStream Stream, string Name) OpenDownload(string? id, string? kind)
    {
        lock (_locked)
        {
            RequireRunning();
            if (_request is not null) throw new ApiError("请在记录保存后下载文件。", 409);
            var item = _history.FirstOrDefault(x => x.Id == id);
            if (item is null || kind is not ("csv" or "report")) throw new ApiError("找不到该采集文件。", 404);
            string filename = kind == "csv" ? CsvName(item.Id) : "capture.json";
            string path = Path.Combine(Session(item.Source, item.Id), filename);
            if (!File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new ApiError("找不到该采集文件。", 404);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            _downloads++;
            return (stream, kind == "csv" ? filename : $"capture_{item.Id[4..]}.json");
        }
    }

    internal void DownloadFinished()
    {
        lock (_locked) _downloads--;
    }

    private void ClearPreview()
    {
        _preview?.Dispose();
        _preview = null;
    }

    public void Dispose()
    {
        ClearPreview();
        // Disposing a Windows process handle does not end the collector.
        _worker?.Dispose();
    }
}
