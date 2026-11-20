using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

string[] options = args.Skip(3).ToArray();
bool hardware = options.Contains("--device");
string[] duration = options.Where(value => value != "--device").ToArray();
int seconds = 0;
if (args.Length < 3 || !int.TryParse(args[2], out int port) || port is < 1 or > 65535 ||
    options.Count(value => value == "--device") > 1 ||
    (duration.Length != 0 && (duration is not ["--seconds", var amount] ||
        !int.TryParse(amount, NumberStyles.None, CultureInfo.InvariantCulture, out seconds) || seconds <= 0)))
{
    Console.Error.WriteLine("用法 QC20daq.Verification <QC20daq.exe> <全新的外置运行目录> <端口> [--device] [--seconds 正整数]");
    return 2;
}

string published = Path.GetFullPath(args[0]);
string root = Path.GetFullPath(args[1]);
if (!File.Exists(published) || (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()))
{
    Console.Error.WriteLine("须指定已发布的 EXE 和全新的隔离运行目录。");
    return 2;
}
string application = Path.Combine(root, "app");
string collector = Path.Combine(application, "collector");
Directory.CreateDirectory(collector);
string program = Path.Combine(application, "QC20daq.exe");
File.Copy(published, program);
foreach (string file in new[] { "Qc20Capture.exe", "Qc20Capture.exe.config" })
    File.Copy(Path.Combine(Path.GetDirectoryName(published)!, "collector", file), Path.Combine(collector, file));
using var suite = new Verification(program, root, port, hardware);
try
{
    var result = seconds == 0 ? await suite.RunAsync() : await suite.RunLongRecordAsync(seconds);
    string json = JsonSerializer.Serialize(result, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    });
    await File.WriteAllTextAsync(Path.Combine(root, "verification.json"), json);
    Console.WriteLine(json);
    return 0;
}
catch (Exception error)
{
    await File.WriteAllTextAsync(Path.Combine(root, "verification_failed.txt"), error.ToString());
    Console.Error.WriteLine(error);
    return 1;
}
finally { await suite.CleanupAsync(); }

internal sealed class Verification(string program, string root, int port, bool hardware) : IDisposable
{
    private readonly HttpClient _client = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
    private Process? _host;
    private string _token = "";
    private string Origin => $"http://127.0.0.1:{port}";
    private string Source => hardware ? "device" : "simulated";
    private string Control => Path.Combine(root, "rotary", "qc20daq");
    private string CaptureRoot => Path.Combine(Path.GetDirectoryName(program)!, "data", Source);

    internal async Task<object> RunAsync()
    {
        await StartHostAsync();
        var initial = await StatusAsync();
        Require(initial.Text("phase") == "idle" && !initial.Flag("busy"), "独立服务应处于待机。");
        Require(initial.GetProperty("history").GetArrayLength() == 0, "独立运行目录应没有记录。");

        await CheckSecondLaunchAsync();
        await PostAsync("/api/connect", new { source = Source, target = "trace", rate = "native" },
            HttpStatusCode.Forbidden, "https://invalid.example");
        await PostAsync("/api/shutdown", new { }, HttpStatusCode.Forbidden, "https://invalid.example");
        await PostAsync("/api/record", new { seconds = 3 }, HttpStatusCode.Conflict);

        var connected = await ConnectAsync();
        string link = connected.GetProperty("connection").Text("id");
        Require(connected.GetProperty("capture").ValueKind == JsonValueKind.Null, "预览创建了采集会话。");
        Require(!Directory.EnumerateDirectories(CaptureRoot).Any(), "预览创建了落盘目录。");
        Console.WriteLine($"原生 1000 Hz，连接后预览正常。来源 {Source}");

        string recordId = (await PostAsync("/api/record",
            new { connection_id = link, seconds = 60 })).Text("id");
        string csv = CsvPath(recordId);
        await WaitStatusAsync(s => s.Flag("recording_active") && s.GetProperty("capture").Long("samples_written") >= 2300);

        var load = Enumerable.Range(0, 4).Select(async _ =>
        {
            var clock = Stopwatch.StartNew();
            int total = 0;
            while (clock.Elapsed < TimeSpan.FromSeconds(1.6))
            {
                var status = await StatusAsync();
                Require(status.Flag("recording_active"), "并行状态刷新期间记录停止。");
                total++;
                await Task.Delay(110);
            }
            return total;
        });
        int requests = (await Task.WhenAll(load)).Sum();

        var journal = await JournalAsync();
        Require(journal.Text("id") == link, "采集进程登记与连接不一致。");
        int workerPid = journal.GetProperty("pid").GetInt32();
        long identity = journal.Long("identity");
        long before = new FileInfo(csv).Length;
        int previousWebPid = _host!.Id;
        await StopHostAsync();
        await WaitForAsync(() => Task.FromResult(new FileInfo(csv).Length), n => n > before, TimeSpan.FromSeconds(5));
        long continued = new FileInfo(csv).Length;
        // A second flush while the Web host is down verifies ongoing sampling.
        long after = await WaitForAsync(() => Task.FromResult(new FileInfo(csv).Length), n => n > continued, TimeSpan.FromSeconds(5));
        using (var worker = Process.GetProcessById(workerPid))
            Require(!worker.HasExited && worker.StartTime.ToUniversalTime().ToFileTimeUtc() == identity, "Web 退出后原采集进程未存活。");
        Console.WriteLine("Web 停止后原采集进程仍持续写盘。");

        await StartHostAsync();
        var recovered = await WaitStatusAsync(s => s.Flag("recording_active") &&
            s.GetProperty("capture").Text("id") == recordId &&
            s.GetProperty("capture").Long("samples_written") >= (hardware ? 15_000 : 7000));
        Require(recovered.GetProperty("connection").Text("id") == link && _host!.Id != previousWebPid, "重启未恢复同一连接。");
        Require((await JournalAsync()).GetProperty("pid").GetInt32() == workerPid, "重启后更换了采集进程。");
        await PostAsync("/api/stop", new { id = recordId });
        var manual = await CheckRecordingAsync(recordId, "operator_stop");

        var saved = await WaitStatusAsync(s => s.Text("phase") == "previewing");
        long last = saved.GetProperty("preview").Long("count");
        var continuedPreview = await WaitStatusAsync(s => s.GetProperty("preview").Long("count") >= last + 600);
        Require(continuedPreview.GetProperty("connection").Text("id") == link &&
            continuedPreview.GetProperty("capture").Long("samples_written") == manual.Samples,
            "保存后的预览会话或已保存点数错误。");

        string timedId = (await PostAsync("/api/record", new { connection_id = link, seconds = 2.5 })).Text("id");
        var timed = await CheckRecordingAsync(timedId, "duration_reached");
        Require(timed.StreamStartTicks == manual.StreamStartTicks, "第二段记录重新启动了设备流。");
        Console.WriteLine($"已保存 {manual.Samples} 点和 {timed.Samples} 点，采集进程报告主机队列丢点均为 0。");

        await DisconnectAsync(link);
        var reconnected = await ConnectAsync();
        string newLink = reconnected.GetProperty("connection").Text("id");
        Require(newLink != link && reconnected.GetProperty("capture").ValueKind == JsonValueKind.Null, "重连未建立新预览会话。");
        Require(reconnected.GetProperty("preview").Long("count") > 0, "重连缺少预览数据。");
        int recoveredWebPid = _host!.Id;
        var savedOnExit = await CheckShutdownWhileRecordingAsync(newLink);

        return new
        {
            Passed = true, Hardware = hardware, RateHz = 1000,
            SingleLaunchReusedWeb = true, PreviewBeforeRecording = true, PreviewAfterSave = true,
            NormalDisconnectReconnect = true, RecoveredSameWorker = true, WorkerPid = workerPid,
            WebPidBeforeRestart = previousWebPid, WebPidAfterRestart = recoveredWebPid,
            BytesBeforeWebShutdown = before, BytesWhileWebDown = after,
            ParallelStatusRequests = requests, FinalState = "idle", Runtime = root, Manual = manual, Timed = timed,
            SavedOnExplicitShutdown = savedOnExit,
        };
    }

    private async Task<RecordingEvidence> CheckShutdownWhileRecordingAsync(string link)
    {
        string id = (await PostAsync("/api/record", new { connection_id = link, seconds = (double?)null })).Text("id");
        var writing = await WaitStatusAsync(s => s.Flag("recording_active") &&
            s.GetProperty("capture").Long("samples_written") >= 2000);
        var journal = await JournalAsync();
        using var worker = Process.GetProcessById(journal.GetProperty("pid").GetInt32());
        Require(journal.Text("id") == link && worker.StartTime.ToUniversalTime().ToFileTimeUtc() == journal.Long("identity"),
            "关闭服务测试未找到对应采集进程。");

        await PostAsync("/api/shutdown", new { });
        await _host!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var ended = JsonSerializer.Deserialize<JsonElement>(
            await File.ReadAllBytesAsync(Path.Combine(Control, link, "ended.json")));
        Require(_host.ExitCode == 0 && worker.HasExited && ended.Text("state") == "closed",
            "关闭服务后 Web 或采集进程未正常退出。");

        await StopHostAsync();
        await StartHostAsync();
        var idle = await StatusAsync();
        Require(idle.Text("phase") == "idle" && !idle.Flag("busy"), "重新启动后服务未回到空闲。");
        var result = await CheckRecordingAsync(id, "operator_stop");
        Require(result.Samples >= writing.GetProperty("capture").Long("samples_written"), "服务退出时丢失已记录样本。");
        Console.WriteLine($"关闭服务前保存 {result.Samples} 点，Web 与采集进程均已退出，重启后 CSV 可下载。");
        return result;
    }

    internal async Task<object> RunLongRecordAsync(int seconds)
    {
        await StartHostAsync();
        var initial = await StatusAsync();
        Require(initial.Text("phase") == "idle" && !initial.Flag("busy") &&
            initial.GetProperty("history").GetArrayLength() == 0, "新运行目录应没有设备会话或记录。");
        var connected = await ConnectAsync(requireInRange: false);
        string link = connected.GetProperty("connection").Text("id");
        var ready = JsonSerializer.Deserialize<JsonElement>(
            await File.ReadAllBytesAsync(Path.Combine(Control, link, "ready.json")));
        Require(ready.Text("preview_source") == "continuous_displacement_stream" &&
            !ready.Flag("periodic_displacement_reads_enabled"), "当前预览未使用修复后的连续流。");
        Require(connected.GetProperty("capture").ValueKind == JsonValueKind.Null, "预览创建了采集记录。");
        var journal = await JournalAsync();
        Require(journal.Text("id") == link, "采集进程登记与连接不一致。");
        int workerPid = journal.GetProperty("pid").GetInt32();
        long workerIdentity = journal.Long("identity");
        long previewBeforeRecord = connected.GetProperty("preview").Long("count");
        var timer = Stopwatch.StartNew();
        string id = (await PostAsync("/api/record", new { connection_id = link, seconds })).Text("id");
        Console.WriteLine($"已开始 {seconds} 秒连续采集检验，原生 1000 Hz，来源 {Source}，会话 {id}，初始位移 {connected.GetProperty("preview").Double("latest_um"):F2} µm。");

        string trace = Path.Combine(root, "long_record_status.jsonl");
        int statusCount = 0;
        int progressAfter = 300;
        long lastWritten = 0;
        JsonElement saved;
        await using (var observations = new StreamWriter(trace) { AutoFlush = true })
        {
            while (true)
            {
                Require(timer.Elapsed.TotalSeconds < seconds + 45.0, "定时记录未在限定时间内完成。");
                var status = await StatusAsync();
                var capture = status.GetProperty("capture");
                string phase = status.Text("phase");
                long previewCount = status.GetProperty("preview").Long("count");
                long written = capture.ValueKind == JsonValueKind.Object ? capture.Long("samples_written") : 0;
                double elapsed = timer.Elapsed.TotalSeconds;
                await observations.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    utc = DateTimeOffset.UtcNow, elapsed_s = elapsed, phase,
                    preview_count = previewCount, samples_written = written, message = status.Text("message"),
                }));
                statusCount++;
                Require(phase != "interrupted" && status.GetProperty("connection").ValueKind == JsonValueKind.Object &&
                    status.GetProperty("connection").Text("id") == link, "实机记录期间中断 " + status.Text("message"));
                Require(capture.Text("id") == id && written >= lastWritten, "采集会话或计数发生异常变化。");
                lastWritten = written;
                if (!status.Flag("recording_active") && capture.GetProperty("report").ValueKind == JsonValueKind.Object)
                {
                    saved = status;
                    break;
                }
                if (elapsed >= progressAfter)
                {
                    Console.WriteLine($"已运行 {elapsed:F0} s，已写入 {written} 点。");
                    progressAfter += 300;
                }
                await Task.Delay(2000);
            }
        }

        double completionElapsed = timer.Elapsed.TotalSeconds;
        Require(saved.Text("phase") == "previewing" && saved.GetProperty("capture").Text("state") == "complete",
            "定时保存后未回到原连接的实时预览。");
        long previewAtSave = saved.GetProperty("preview").Long("count");
        Console.WriteLine($"定时保存完成，{lastWritten} 点。检查保存后的 30 秒预览。");
        var previewTimer = Stopwatch.StartNew();
        long previewAfter = previewAtSave;
        do
        {
            await Task.Delay(1000);
            var status = await StatusAsync();
            Require(!status.Flag("recording_active") && status.Text("phase") == "previewing" &&
                status.GetProperty("connection").Text("id") == link &&
                status.GetProperty("capture").Text("id") == id &&
                status.GetProperty("capture").Long("samples_written") == lastWritten,
                "正常保存后原连接未保持预览。");
            previewAfter = status.GetProperty("preview").Long("count");
        } while (previewTimer.Elapsed.TotalSeconds < 30);
        double previewSeconds = previewTimer.Elapsed.TotalSeconds;
        Require(previewAfter - previewAtSave >= 29_000, "保存后持续预览的样本量不足。");
        using (var worker = Process.GetProcessById(workerPid))
            Require(!worker.HasExited && worker.StartTime.ToUniversalTime().ToFileTimeUtc() == workerIdentity &&
                (await JournalAsync()).Long("pid") == workerPid, "记录前后更换了 32 位采集进程。");
        await DisconnectAsync(link);
        var record = await CheckRecordingAsync(id, "duration_reached", requireInRange: false);
        Require(record.HostRxSeconds >= seconds, "实际主机首末点跨度未达到设定时长。");
        return new
        {
            Passed = true, Hardware = hardware, RateHz = 1000, RequestedSeconds = seconds, Runtime = root,
            WorkerPid = workerPid, WebPid = _host!.Id, ContinuousPreviewSource = true, PeriodicDisplacementReads = false,
            Record = record, CompletionElapsedSeconds = completionElapsed, StatusObservations = statusCount,
            PreviewBeforeRecord = previewBeforeRecord, PreviewAtSave = previewAtSave, PreviewAfterSave = previewAfter,
            PreviewAddedAfterSave = previewAfter - previewAtSave, PreviewSecondsAfterSave = previewSeconds, FinalState = "idle",
        };
    }

    private async Task<JsonElement> ConnectAsync(bool requireInRange = true)
    {
        var response = await PostAsync("/api/connect", new { source = Source, target = "trace", rate = "native" });
        var status = await WaitStatusAsync(s => s.Text("phase") == "interrupted" ||
            s.Text("phase") == "previewing" && s.GetProperty("preview").Long("count") >= 1000, seconds: 90);
        Require(status.Text("phase") == "previewing", "连接失败 " + status.Text("message"));
        var preview = status.GetProperty("preview");
        Require(status.GetProperty("connection").Text("id") == response.Text("id") &&
            status.GetProperty("connection").Double("rate_hz") == 1000 &&
            preview.GetProperty("points").GetArrayLength() > 0 &&
            preview.GetProperty("points").GetArrayLength() <= 900 &&
            preview.GetProperty("latest_um").ValueKind == JsonValueKind.Number,
            "连接后未获得完整的原生率预览。");
        if (hardware && requireInRange)
        {
            var device = status.GetProperty("connection").GetProperty("device");
            double latest = preview.Double("latest_um");
            Require(latest > device.Double("measurement_min_mm") * 1000 && latest < device.Double("measurement_max_mm") * 1000,
                $"实机读数 {latest:F3} µm 位于量程边界，量程内验收待补。");
        }
        return status;
    }

    private async Task DisconnectAsync(string link)
    {
        await PostAsync("/api/disconnect", new { id = link });
        var idle = await WaitStatusAsync(s => !s.Flag("busy"));
        Require(idle.Text("phase") == "idle" && idle.GetProperty("connection").ValueKind == JsonValueKind.Null &&
            idle.GetProperty("preview").Long("count") == 0, "断开后预览没有清空。");
    }

    private async Task<RecordingEvidence> CheckRecordingAsync(string id, string stopReason, bool requireInRange = true)
    {
        var status = await WaitStatusAsync(s => !s.Flag("recording_active") &&
            s.GetProperty("capture").ValueKind == JsonValueKind.Object && s.GetProperty("capture").Text("id") == id);
        var report = status.GetProperty("capture").GetProperty("report");
        Require(report.Text("state") == "complete" && report.Text("stop_reason") == stopReason &&
            report.Text("source") == Source && report.Text("recording_mode") == "connected_stream_gate", "采集结束状态异常。");
        long samples = report.Long("samples_written");
        Require(samples > 1000 && samples == report.Long("samples_received") &&
            report.Long("host_queue_dropped") == 0 && report.Long("non_finite_samples_received") == 0 &&
            report.GetProperty("errors").GetArrayLength() == 0 && report.Double("nominal_sample_rate_hz") == 1000,
            "收点数、写入数、状态或无效点不合格。");
        if (requireInRange)
            Require(report.Text("range_check") == "no_limit_observed", "本段采集发生量程触限。");

        string csv = CsvPath(id);
        using var reader = new StreamReader(csv);
        Require(await reader.ReadLineAsync() == "sample_index,time_s,length_delta_um,host_rx_elapsed_s", "CSV 列头不一致。");
        long index = 0;
        double previousRx = -1, minimum = double.PositiveInfinity, maximum = double.NegativeInfinity;
        var culture = CultureInfo.InvariantCulture;
        while (await reader.ReadLineAsync() is string line)
        {
            string[] fields = line.Split(',');
            Require(fields.Length == 4 && long.Parse(fields[0], culture) == index, "CSV 点序不连续。");
            double time = double.Parse(fields[1], culture);
            double displacement = double.Parse(fields[2], culture);
            double rx = double.Parse(fields[3], culture);
            Require(Math.Abs(time - index / 1000.0) < 1e-10 && double.IsFinite(displacement)
                && double.IsFinite(rx) && rx >= previousRx, "CSV 数值或时标异常。");
            minimum = Math.Min(minimum, displacement);
            maximum = Math.Max(maximum, displacement);
            previousRx = rx;
            index++;
        }
        Require(index == samples, "CSV 行数与采集报告不符。");
        using var response = await _client.GetAsync(Origin + $"/api/download?id={Uri.EscapeDataString(id)}&file=csv",
            HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        Require(response.Content.Headers.ContentDisposition?.FileName?.Trim('"') == Path.GetFileName(csv),
            "网页下载的文件名缺少对应的时间戳。");
        await using var download = await response.Content.ReadAsStreamAsync();
        await using var savedFile = File.OpenRead(csv);
        byte[] localHash = await SHA256.HashDataAsync(savedFile);
        Require((await SHA256.HashDataAsync(download)).SequenceEqual(localHash), "网页下载与写盘原件不一致。");

        var clock = report.GetProperty("host_timing");
        Require(clock.Long("record_gate_open_ticks") <= clock.Long("first_written_callback_ticks") &&
            clock.Long("last_written_callback_ticks") <= clock.Long("record_gate_close_ticks"), "主机启停时标顺序异常。");
        return new(id, samples, report.Double("nominal_sample_span_s"), minimum, maximum, stopReason,
            report.Long("host_queue_dropped"), clock.Long("live_stream_start_call_ticks"),
            report.Double("host_rx_span_s"), report.Double("max_host_rx_gap_s"),
            report.GetProperty("at_lower_limit_samples_received").ValueKind == JsonValueKind.Number
                ? report.Long("at_lower_limit_samples_received") : null,
            report.GetProperty("at_upper_limit_samples_received").ValueKind == JsonValueKind.Number
                ? report.Long("at_upper_limit_samples_received") : null,
            report.Text("device_status_text"), Convert.ToHexStringLower(localHash));
    }

    private string CaptureFolder(string id)
    {
        Require(Regex.IsMatch(id, @"\Aweb_[0-9]{8}_[0-9]{6}\z"), "会话目录名异常。");
        return Path.Combine(CaptureRoot, id);
    }

    private string CsvPath(string id) => Path.Combine(CaptureFolder(id), $"ballbar_{id[4..]}.csv");

    private Process Launch()
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--no-browser", "--port", port.ToString(CultureInfo.InvariantCulture), "--runtime", root })
            start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new IOException("发布程序启动失败。");
    }

    private async Task StartHostAsync()
    {
        _host = Launch();
        var ready = await WaitForAsync(async () =>
        {
            try { return await GetAsync("/api/health"); }
            catch (HttpRequestException) when (!_host.HasExited) { return default; }
        }, s => s.ValueKind == JsonValueKind.Object, TimeSpan.FromSeconds(15));
        Require(ready.Text("application") == "QC20daq" && ready.Long("process_id") == _host.Id,
            "端口已被其他进程占用。");
        _token = (await StatusAsync()).Text("token");
    }

    private async Task CheckSecondLaunchAsync()
    {
        using var other = Launch();
        await other.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Require(other.ExitCode == 0 && (await GetAsync("/api/health")).Long("process_id") == _host!.Id,
            "重复启动未复用正在运行的本机服务。");
    }

    private async Task StopHostAsync()
    {
        if (_host is null) return;
        if (!_host.HasExited)
        {
            _host.Kill(entireProcessTree: false);
            await _host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        _host.Dispose();
        _host = null;
    }

    private async Task<JsonElement> PostAsync(string path, object body, HttpStatusCode expected = HttpStatusCode.Accepted,
        string? requestOrigin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Origin + path) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("Origin", requestOrigin ?? Origin);
        request.Headers.TryAddWithoutValidation("X-QC20-Token", _token);
        using var response = await _client.SendAsync(request);
        string data = await response.Content.ReadAsStringAsync();
        Require(response.StatusCode == expected, $"{path} 返回 {(int)response.StatusCode}，期望 {(int)expected}。{data}");
        return JsonSerializer.Deserialize<JsonElement>(data);
    }

    private async Task<JsonElement> GetAsync(string path) =>
        await _client.GetFromJsonAsync<JsonElement>(Origin + path);

    private Task<JsonElement> StatusAsync() => GetAsync("/api/status");

    private Task<JsonElement> WaitStatusAsync(Func<JsonElement, bool> accept, int seconds = 18) =>
        WaitForAsync(StatusAsync, accept, TimeSpan.FromSeconds(seconds));

    private async Task<JsonElement> JournalAsync() =>
        JsonSerializer.Deserialize<JsonElement>(await File.ReadAllBytesAsync(Path.Combine(Control, "last_job.json")));

    private static async Task<T> WaitForAsync<T>(Func<Task<T>> read, Func<T, bool> accept, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        do
        {
            T status = await read();
            if (accept(status)) return status;
            await Task.Delay(100);
        } while (clock.Elapsed < timeout);
        throw new TimeoutException("等待采集状态超时。");
    }

    internal async Task CleanupAsync()
    {
        string journal = Path.Combine(Control, "last_job.json");
        if (File.Exists(journal))
        {
            try
            {
                var job = await JournalAsync();
                string id = job.Text("id");
                if (job.Text("kind") == "live" && Regex.IsMatch(id, @"\Alive_[a-f0-9]{24}\z"))
                {
                    using var worker = Process.GetProcessById(job.GetProperty("pid").GetInt32());
                    if (!worker.HasExited && worker.StartTime.ToUniversalTime().ToFileTimeUtc() == job.Long("identity"))
                    {
                        using (File.Open(Path.Combine(Control, id, "disconnect.requested"), FileMode.OpenOrCreate)) { }
                        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
                    }
                }
            }
            catch (ArgumentException) { } // The collector already exited.
            catch (Exception error) { Console.Error.WriteLine("结束测试会话 " + error.Message); }
        }
        await StopHostAsync();
    }

    public void Dispose() => _client.Dispose();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    internal sealed record RecordingEvidence(string Id, long Samples, double Seconds, double MinUm, double MaxUm,
        string StopReason, long HostQueueDropped, long StreamStartTicks, double HostRxSeconds,
        double MaxHostGapSeconds, long? LowerLimitSamples, long? UpperLimitSamples, string DeviceStatus, string CsvSha256);
}

internal static class JsonFields
{
    internal static string Text(this JsonElement value, string name) => value.GetProperty(name).GetString() ?? "";
    internal static long Long(this JsonElement value, string name) => value.GetProperty(name).GetInt64();
    internal static double Double(this JsonElement value, string name) => value.GetProperty(name).GetDouble();
    internal static bool Flag(this JsonElement value, string name) => value.GetProperty(name).GetBoolean();
}
