using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Renishaw.IO.ChannelFilters;

internal sealed class LiveTap : ChannelFilter
{
    internal const int Capacity = 16384;
    private readonly object gate = new object();
    private readonly double[] samples = new double[Capacity];
    private long count, lastReceive;
    private CsvSink recorder;

    internal long LastReceiveTicks { get { return Interlocked.Read(ref lastReceive); } }

    public override double Process(double value)
    {
        lock (gate)
        {
            if (recorder != null) recorder.Process(value);
            samples[count % Capacity] = value;
            count++;
            Interlocked.Exchange(ref lastReceive, Stopwatch.GetTimestamp());
        }
        return value;
    }

    internal long SetRecorder(CsvSink sink)
    {
        lock (gate)
        {
            recorder = sink;
            return Stopwatch.GetTimestamp();
        }
    }

    internal bool TryGetLatest(out double value)
    {
        lock (gate)
        {
            value = count == 0 ? double.NaN : samples[(count - 1) % Capacity];
            return count != 0;
        }
    }

    internal long CopyNew(long since, double[] buffer, out int length)
    {
        lock (gate)
        {
            length = (int)Math.Min(count - since, Capacity);
            int start = (int)((count - length) % Capacity);
            int beforeWrap = Math.Min(length, Capacity - start);
            Array.Copy(samples, start, buffer, 0, beforeWrap);
            Array.Copy(samples, 0, buffer, beforeWrap, length - beforeWrap);
            return count;
        }
    }
}

internal sealed class LivePreviewMemory : IDisposable
{
    // Keep the layout in sync with qc20_web_preview.py. Readers retry when the sequence
    // is odd or changes. The callback accesses only LiveTap, never this shared mapping.
    private const int HeaderBytes = 32;
    private readonly MemoryMappedFile mapping;
    private readonly MemoryMappedViewAccessor view;
    private readonly double[] buffer = new double[LiveTap.Capacity];
    private long published, token, written;
    private int sequence;

    internal LivePreviewMemory(string name)
    {
        mapping = MemoryMappedFile.CreateNew(name, HeaderBytes + LiveTap.Capacity * sizeof(double));
        view = mapping.CreateViewAccessor();
    }

    internal void Publish(LiveTap tap, long recordToken, long recordWritten)
    {
        int length;
        long end = tap.CopyNew(published, buffer, out length);
        if (length == 0 && recordToken == token && recordWritten == written) return;

        view.Write(0, unchecked(++sequence));
        Thread.MemoryBarrier();
        int start = (int)((end - length) % LiveTap.Capacity);
        int beforeWrap = Math.Min(length, LiveTap.Capacity - start);
        if (beforeWrap > 0) view.WriteArray(HeaderBytes + start * sizeof(double), buffer, 0, beforeWrap);
        if (length > beforeWrap) view.WriteArray(HeaderBytes, buffer, beforeWrap, length - beforeWrap);
        view.Write(8, end);
        view.Write(16, recordToken);
        view.Write(24, recordWritten);
        Thread.MemoryBarrier();
        view.Write(0, unchecked(++sequence));
        published = end;
        token = recordToken;
        written = recordWritten;
    }

    public void Dispose()
    {
        view.Dispose();
        mapping.Dispose();
    }
}

internal sealed class LiveRecordRequest
{
    public string id { get; set; }
    public long token { get; set; }
    public double? seconds { get; set; }
}

internal sealed class LiveRecording
{
    internal readonly long Token;
    internal CsvSink Sink { get; private set; }
    private readonly string directory;
    private readonly string stopFile;
    private readonly string csvName;
    private readonly double? seconds;
    private readonly Dictionary<string, object> report;
    private readonly Dictionary<string, object> timing;
    private readonly List<string> errors = new List<string>();

    internal LiveRecording(string directory, LiveRecordRequest request, string rate, double actual, int factor,
        string source, object device, object files, long streamStartCall, long streamStartReturn)
    {
        this.directory = directory;
        stopFile = Path.Combine(directory, "stop.requested");
        csvName = "ballbar_" + request.id.Substring("web_".Length) + ".csv";
        Token = request.token;
        seconds = request.seconds;
        report = CaptureApp.NewReport(seconds, rate, source);
        report["recording_mode"] = "connected_stream_gate";
        report["preview_source"] = "continuous_displacement_stream";
        report["periodic_displacement_reads_enabled"] = false;
        report["device"] = device;
        report["downsampling_factor"] = factor;
        report["nominal_sample_rate_hz"] = actual;
        timing = (Dictionary<string, object>)report["host_timing"];
        timing["live_stream_start_call_ticks"] = streamStartCall;
        timing["live_stream_start_return_ticks"] = streamStartReturn;
        CaptureApp.SaveJson(directory, "session.json", new {
            schema = "qc20_capture_session_v1", mode = "record", source = source,
            recording_mode = "connected_stream_gate", request_utc = DateTime.UtcNow.ToString("O", CaptureApp.Invariant),
            requested_seconds = seconds, requested_rate = rate, files = files,
        });
    }

    internal bool Start(LiveTap tap, DeviceSession session, string linkFault, bool disconnecting)
    {
        if (linkFault != null)
        {
            Finish(tap, session, linkFault);
            return false;
        }
        if (disconnecting || File.Exists(stopFile))
        {
            Finish(tap, session, "stopped_before_recording");
            return false;
        }
        try
        {
            CaptureApp.SaveJson(directory, "ready.json", new { device = report["device"],
                nominal_sample_rate_hz = report["nominal_sample_rate_hz"],
                downsampling_factor = report["downsampling_factor"], source = report["source"] });
            Sink = new CsvSink(Path.Combine(directory, csvName), (double)report["nominal_sample_rate_hz"],
                               session.MinimumReadingMm, session.MaximumReadingMm);
            timing["rx_elapsed_origin_ticks"] = Sink.OriginTicks;
            timing["utc_before_start"] = CaptureApp.ReadClockAnchor();
            report["host_record_command_utc"] = DateTime.UtcNow.ToString("O", CaptureApp.Invariant);
            timing["record_gate_open_ticks"] = tap.SetRecorder(Sink);
            return true;
        }
        catch (IOException error)
        {
            errors.Add(error.GetType().Name + ": " + error.Message);
            Finish(tap, session, "setup_error");
            return false;
        }
    }

    internal string StopReason(string linkFault, bool disconnecting)
    {
        string fault = Sink.Fault ?? linkFault;
        if (fault != null) return fault;
        if (disconnecting || File.Exists(stopFile)) return "operator_stop";
        // A wall-clock deadline alone can fall inside a stalled stream.
        if (seconds.HasValue && Sink.FirstReceive >= 0 && Sink.LastReceive - Sink.FirstReceive >= seconds.Value)
            return "duration_reached";
        return null;
    }

    internal void Finish(LiveTap tap, DeviceSession session, string reason)
    {
        CsvSink sink = Sink;
        Sink = null;
        if (sink != null)
        {
            timing["record_gate_close_ticks"] = tap.SetRecorder(null);
            try { sink.Finish(); }
            catch (Exception error) { errors.Add("CSV close: " + error.Message); }
            sink.AddStatistics(report);
            timing["first_written_callback_ticks"] = sink.FirstWrittenTicks;
            timing["last_written_callback_ticks"] = sink.LastWrittenTicks;
            timing["utc_after_capture"] = CaptureApp.ReadClockAnchor();
            if (sink.Fault != null && sink.Fault != reason) errors.Add(sink.Fault);
            if ((reason == "duration_reached" || reason == "operator_stop") && sink.Written >= 2 && errors.Count == 0)
                report["state"] = "complete";
        }
        report["device_status_or"] = (int)session.StatusSeen;
        report["device_status_text"] = session.StatusSeen.ToString();
        report["stop_reason"] = reason;
        report["errors"] = errors;
        report["closed_utc"] = DateTime.UtcNow.ToString("O", CaptureApp.Invariant);
        CaptureApp.SaveJson(directory, "capture.json", report);
    }
}

internal static class LiveCapture
{
    private static string StreamFault(DeviceSession session, LiveTap tap, long streamStart, double rate)
    {
        string fault = session.PollFault();
        if (fault != null) return fault;
        long lastReceive = Math.Max(tap.LastReceiveTicks, streamStart);
        double silence = (Stopwatch.GetTimestamp() - lastReceive) / (double)Stopwatch.Frequency;
        return silence >= Math.Max(5.0, 10.0 / rate) ? "no_recent_readings" : null;
    }

    internal static int Run(string control, string root, string requestedRate, Func<DeviceSession> openTarget,
        Func<bool> cancelled, string source)
    {
        var serializer = new JavaScriptSerializer();
        object files = serializer.DeserializeObject(File.ReadAllText(Path.Combine(control, "provenance.json")));
        string requestFile = Path.Combine(control, "record.json");
        string mapName = @"Local\DynamicError.QC20.Preview." + new DirectoryInfo(control).Name;
        var tap = new LiveTap();
        DeviceSession session = null;
        LiveRecording recording = null;
        bool startAttempted = false;
        var errors = new List<string>();

        try
        {
            if (cancelled()) throw new OperationCanceledException();
            session = openTarget();
            session.WaitReady(cancelled);
            int factor = CaptureApp.Downsample(session.Device.SampleRate, requestedRate);
            double actual = session.Device.SampleRate / factor;
            using (var preview = new LivePreviewMemory(mapName))
            {
                if (cancelled()) throw new OperationCanceledException();
                startAttempted = true;
                long streamStartCall = Stopwatch.GetTimestamp();
                var start = session.Device.StartRecording(tap, factor, false);
                CaptureApp.Wait(start, "StartRecording");
                long streamStartReturn = Stopwatch.GetTimestamp();
                if (!start.Result) throw new IOException("The device library refused to start streaming.");
                double firstReading;
                while (!tap.TryGetLatest(out firstReading))
                {
                    if (cancelled()) throw new OperationCanceledException();
                    string firstSampleFault = StreamFault(session, tap, streamStartReturn, actual);
                    if (firstSampleFault != null) throw new IOException(firstSampleFault);
                    Thread.Sleep(40);
                }
                object device = session.Describe(firstReading);
                CaptureApp.SaveJson(control, "ready.json", new { device = device, nominal_sample_rate_hz = actual,
                    source = source, preview_map = mapName, preview_source = "continuous_displacement_stream",
                    periodic_displacement_reads_enabled = false });
                DateTime seenRequest = DateTime.MinValue;
                while (true)
                {
                    string fault = StreamFault(session, tap, streamStartReturn, actual);
                    bool disconnecting = cancelled();
                    if (recording == null)
                    {
                        // Starts are rare. Check the control file only between saves.
                        DateTime modified = File.GetLastWriteTimeUtc(requestFile);
                        if (modified != seenRequest && File.Exists(requestFile))
                        {
                            LiveRecordRequest request;
                            using (var file = new FileStream(requestFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                            using (var reader = new StreamReader(file))
                                request = serializer.Deserialize<LiveRecordRequest>(reader.ReadToEnd());
                            if (request == null || request.id == null || request.token <= 0 ||
                                !Regex.IsMatch(request.id, @"\Aweb_[0-9]{8}_[0-9]{6}\z") ||
                                (request.seconds.HasValue && (!CaptureApp.Finite(request.seconds.Value) || request.seconds <= 0)))
                                throw new InvalidDataException("Invalid Web recording request.");
                            seenRequest = modified;
                            recording = new LiveRecording(Path.Combine(root, request.id), request, requestedRate,
                                actual, factor, source, device, files, streamStartCall, streamStartReturn);
                            if (!recording.Start(tap, session, fault, disconnecting)) recording = null;
                        }
                    }
                    else
                    {
                        string reason = recording.StopReason(fault, disconnecting);
                        if (reason != null)
                        {
                            LiveRecording ending = recording;
                            recording = null;
                            ending.Finish(tap, session, reason);
                        }
                    }
                    if (fault != null) throw new IOException(fault);
                    if (disconnecting) break;
                    preview.Publish(tap, recording == null ? 0 : recording.Token,
                        recording == null ? 0 : recording.Sink.Written);
                    Thread.Sleep(40);
                }
            }
        }
        catch (OperationCanceledException error)
        {
            if (!cancelled()) errors.Add(error.GetType().Name + ": " + error.Message);
        }
        catch (Exception error)
        {
            errors.Add(error.GetType().Name + ": " + error.Message);
        }
        finally
        {
            if (recording != null)
            {
                try { recording.Finish(tap, session, errors.Count == 0 ? "operator_stop" : "live_stream_error"); }
                catch (Exception error) { errors.Add("Save: " + error.Message); }
            }
            if (session != null && startAttempted)
            {
                session.IntentionalStop = true;
                try { CaptureApp.Wait(session.Device.StopRecording(), "StopRecording"); }
                catch (Exception error) { errors.Add("Stop: " + error.Message); }
            }
            if (session != null)
            {
                try { session.Dispose(); }
                catch (Exception error) { errors.Add("Close: " + error.Message); }
            }
        }
        CaptureApp.SaveJson(control, "ended.json", new { state = errors.Count == 0 ? "closed" : "interrupted", errors = errors });
        foreach (string error in errors) Console.Error.WriteLine(error);
        return errors.Count == 0 ? 0 : 2;
    }
}
