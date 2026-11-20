using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Renishaw.Calibration;
using Renishaw.Calibration.Ballbar;
using Renishaw.Calibration.Ballbar.Api;
using Renishaw.Calibration.Devices.Infrastructure;
using Renishaw.Calibration.Devices.QC;
using Renishaw.IO;
using Renishaw.IO.Bluetooth;
using Renishaw.IO.Bluetooth.Classic;
using Renishaw.IO.Bluetooth.LowEnergy;
using Renishaw.IO.ChannelFilters;
using Renishaw.IO.SimpleSensorInterface;
using Environment = System.Environment;
using IOException = System.IO.IOException;

internal static class CaptureApp
{
    internal static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
    private static volatile bool stopRequested;

    public static int Main(string[] arguments)
    {
        Console.OutputEncoding = Utf8;
        try
        {
            if (Environment.Is64BitProcess) throw new InvalidOperationException("Use the x86 host.");
            string install = arguments[0];
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                string name = new AssemblyName(args.Name).Name;
                if (!name.StartsWith("Renishaw.", StringComparison.Ordinal) && name != "Microsoft.VisualStudio.Validation")
                    return null;
                string file = Path.Combine(install, name + ".dll");
                return File.Exists(file) ? Assembly.LoadFrom(file) : null;
            };
            Console.CancelKeyPress += (sender, args) => { args.Cancel = true; stopRequested = true; };
            string[] command = arguments.Skip(1).ToArray();
            if (command[0] != "discover" && command[0] != "info" && command[0] != "record" && command[0] != "live")
                return Run(command);
            using (var ownership = new Mutex(false, @"Local\DynamicError.QC20.Device"))
            {
                bool owned;
                try { owned = ownership.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned) throw new InvalidOperationException("QC20 is busy in another collector process.");
                try { return Run(command); }
                finally { ownership.ReleaseMutex(); }
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
            return 1;
        }
    }

    private static int Run(string[] arguments)
    {
        switch (arguments[0])
        {
            case "inspect":
                Print(Inspect());
                return 0;
            case "check":
                Dictionary<string, object> checks = OfflineChecks.Run(arguments[1]);
                SaveJson(arguments[1], "check.json", checks);
                Console.WriteLine("Offline capture checks passed. No Bluetooth or hardware test was performed.");
                return 0;
            case "discover":
                Print(Discover().Select(d => new { model = d.Model, serial = d.SerialNumber, ble = d.IsBle }).ToArray());
                return 0;
            case "info":
                using (DeviceSession session = OpenTarget(arguments.Skip(1).ToArray()))
                {
                    session.WaitReady(() => stopRequested);
                    Print(session.Describe());
                }
                return 0;
            case "live":
            case "live-simulate":
                string control = arguments[1];
                bool simulation = arguments[0] == "live-simulate";
                string disconnect = Path.Combine(control, "disconnect.requested");
                Func<bool> disconnecting = () => stopRequested || File.Exists(disconnect);
                return LiveCapture.Run(control, arguments[2], arguments[3],
                    () => simulation ? OfflineChecks.OpenPreviewDevice(streamOnly: true)
                        : OpenTarget(arguments.Skip(4).ToArray(), disconnecting, streamOnly: true),
                    disconnecting, simulation ? "simulated" : "device");
            case "record":
            case "simulate":
                string directory = arguments[1];
                string stopFile = Path.Combine(directory, "stop.requested");
                Func<bool> cancelled = () => stopRequested || File.Exists(stopFile);
                bool simulated = arguments[0] == "simulate";
                double? seconds = arguments[2] == "manual" ? (double?)null : double.Parse(arguments[2], Invariant);
                var result = Record(directory, seconds, arguments[3],
                    () => simulated ? OfflineChecks.OpenPreviewDevice(streamOnly: false)
                        : OpenTarget(arguments.Skip(4).ToArray(), cancelled),
                    cancelled, simulated ? "simulated" : "device");
                Print(result);
                return (string)result["state"] == "complete" ? 0 : 2;
            default:
                throw new ArgumentException("Unrecognised command.");
        }
    }

    internal static object Inspect()
    {
        var assemblies = new[] {
            typeof(BallbarDevice).Assembly, typeof(QC20Ballbar).Assembly, typeof(QC20Proxy).Assembly,
            typeof(BluetoothLowEnergyEndPoint).Assembly, typeof(ChannelFilter).Assembly,
            typeof(CreateObserverRequest).Assembly,
        };
        var address = new BluetoothAddress(1UL);
        return new {
            process_architecture = "x86",
            bluetooth_endpoints = new[] {
                new BluetoothClassicEndPoint(address, BluetoothGuids.SerialPortServiceClassGuid).GetType().Name,
                new BluetoothLowEnergyEndPoint(address, new[] { Guid.Empty }).GetType().Name,
            },
            loaded_libraries = assemblies.Select(a => new {
                path = a.Location, version = FileVersionInfo.GetVersionInfo(a.Location).FileVersion,
            }).ToArray(),
            hardware_contacted = false,
        };
    }

    private static DiscoveredBluetoothDevice[] Discover(Func<bool> cancelled = null)
    {
        using (var discovery = new QC20Discovery())
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
        {
            if (!discovery.IsBluetoothEnabled) throw new InvalidOperationException("Windows Bluetooth radio is unavailable.");
            var search = discovery.FindDevicesAsync(timeout.Token);
            if (cancelled != null)
            {
                while (!search.IsCompleted && !timeout.IsCancellationRequested)
                {
                    if (cancelled()) { timeout.Cancel(); break; }
                    Thread.Sleep(40);
                }
            }
            search.GetAwaiter().GetResult();
            return discovery.DiscoveredBluetoothDevices.ToArray();
        }
    }

    private static DeviceSession OpenTarget(string[] target, Func<bool> cancelled = null, bool streamOnly = false)
    {
        BluetoothEndPoint endpoint;
        string serial;
        bool ble;
        if (target[0] == "serial")
        {
            var matches = Discover(cancelled).Where(d => string.Equals(d.SerialNumber, target[1], StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("The requested serial number did not match exactly one discovered device.");
            endpoint = matches[0].EndPoint;
            ble = matches[0].IsBle;
            serial = target[1];
        }
        else
        {
            ble = target[0] == "ble";
            var address = new BluetoothAddress(target[1]);
            var service = Guid.Parse(target[2]);
            endpoint = ble ? (BluetoothEndPoint)new BluetoothLowEnergyEndPoint(address, new[] { service })
                           : new BluetoothClassicEndPoint(address, service);
            serial = target[3];
        }
        return new DeviceSession(new QC20BluetoothConnector(new BluetoothConnector(), new RealDelay()),
                                 endpoint, serial, ble, new SleepInhibitor(), streamOnly);
    }

    internal static bool Finite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    [DllImport("kernel32.dll")]
    private static extern void GetSystemTimePreciseAsFileTime(out long fileTime);

    internal static Dictionary<string, object> ReadClockAnchor()
    {
        // Keep the narrowest bracket so an interrupted or first-call read does not set the anchor.
        long bestBefore = 0, bestAfter = long.MaxValue, bestUtc = 0;
        bool precise = true;
        const int attempts = 8;
        for (int i = 0; i < attempts; i++)
        {
            long before = Stopwatch.GetTimestamp();
            long utc;
            if (precise)
            {
                try { GetSystemTimePreciseAsFileTime(out utc); }
                catch (EntryPointNotFoundException)
                {
                    precise = false;
                    utc = DateTime.UtcNow.ToFileTimeUtc();
                }
            }
            else utc = DateTime.UtcNow.ToFileTimeUtc();
            long after = Stopwatch.GetTimestamp();
            if (after - before < bestAfter - bestBefore)
            {
                bestBefore = before;
                bestAfter = after;
                bestUtc = utc;
            }
        }
        return new Dictionary<string, object> {
            {"counter_before_ticks", bestBefore},
            {"counter_after_ticks", bestAfter},
            {"utc_filetime_100ns", bestUtc.ToString(Invariant)},
            {"utc_source", precise ? "GetSystemTimePreciseAsFileTime" : "DateTime.UtcNow"},
            {"brackets_measured", attempts},
        };
    }

    internal static int Downsample(double deviceRate, string rate)
    {
        // A very large value is also the library's fallback for missing device configuration.
        if (!Finite(deviceRate) || deviceRate <= 0 || deviceRate > 1000.000001)
            throw new InvalidDataException("No supported QC20 native rate was reported by the device.");
        if (rate == "native") return 1;
        double asked = double.Parse(rate, Invariant);
        double factor = deviceRate / asked;
        double rounded = Math.Round(factor);
        if (!Finite(factor) || rounded < 1 || rounded > 32767 || Math.Abs(factor - rounded) > 1e-8)
            throw new ArgumentException("Requested rate must divide the reported native rate exactly. Use native or an integer divisor.");
        return (int)rounded;
    }

    internal static void Wait(Task operation, string name)
    {
        if (Task.WhenAny(operation, Task.Delay(15000)).GetAwaiter().GetResult() != operation)
            throw new TimeoutException(name + " timed out.");
        operation.GetAwaiter().GetResult();
    }

    internal static Dictionary<string, object> NewReport(double? seconds, string rate, string source)
    {
        var hostTiming = new Dictionary<string, object> {
            {"counter_source", Stopwatch.IsHighResolution ? "windows_qpc" : "stopwatch_fallback"},
            {"counter_frequency_hz", Stopwatch.Frequency},
            {"meaning", "Host callback and software events. The device acquisition epoch is not known."},
        };
        return new Dictionary<string, object> {
            {"schema", "qc20_capture_v1"}, {"source", source}, {"state", "interrupted"},
            {"requested_seconds", seconds}, {"requested_rate", rate},
            {"sample_rate_verified", false}, {"sample_continuity_verified", false},
            {"time_s_basis", "sample_index / nominal_sample_rate_hz"},
            {"host_rx_elapsed_s_basis", "monotonic host callback time, includes Bluetooth batching"},
            {"length_delta_um_basis", "QC20Ballbar library relative displacement in mm multiplied by 1000"},
            {"averaging_on", false}, {"hardware_timestamp_available", false},
            {"host_timing", hostTiming},
        };
    }

    internal static Dictionary<string, object> Record(string directory, double? seconds, string rate,
        Func<DeviceSession> openTarget, Func<bool> cancelled, string source)
    {
        var report = NewReport(seconds, rate, source);
        var hostTiming = (Dictionary<string, object>)report["host_timing"];
        DeviceSession session = null;
        CsvSink sink = null;
        bool startAttempted = false;
        var errors = new List<string>();
        string reason = "setup_error";
        try
        {
            if (cancelled()) throw new OperationCanceledException("Stopped before connecting.");
            session = openTarget();
            session.WaitReady(cancelled);
            var device = session.Device;
            report["device"] = session.Describe();
            int factor = Downsample(device.SampleRate, rate);
            double actual = device.SampleRate / factor;
            report["downsampling_factor"] = factor;
            report["nominal_sample_rate_hz"] = actual;
            if (cancelled()) throw new OperationCanceledException("Stopped before recording.");
            // A single metadata write before acquisition lets monitoring read the negotiated rate and range.
            SaveJson(directory, "ready.json", new { device = report["device"], nominal_sample_rate_hz = actual,
                                                    downsampling_factor = factor, source = source });
            sink = new CsvSink(Path.Combine(directory, "ballbar.csv"), actual,
                               session.MinimumReadingMm, session.MaximumReadingMm);
            hostTiming["rx_elapsed_origin_ticks"] = sink.OriginTicks;
            hostTiming["utc_before_start"] = ReadClockAnchor();
            report["host_record_command_utc"] = DateTime.UtcNow.ToString("O", Invariant);
            startAttempted = true;
            reason = "start_error";
            hostTiming["start_call_ticks"] = Stopwatch.GetTimestamp();
            var start = device.StartRecording(sink, factor, false);
            Wait(start, "StartRecording");
            hostTiming["start_return_ticks"] = Stopwatch.GetTimestamp();
            if (!start.Result) throw new IOException("The device library refused to start recording.");
            Console.WriteLine("{0} at nominal {1:R} Hz. CSV: {2}",
                source == "simulated" ? "Simulated capture" : "Recording", actual, Path.Combine(directory, "ballbar.csv"));
            reason = "capture_error";
            reason = CaptureUntil(sink, session, seconds, cancelled);
        }
        catch (Exception error)
        {
            if (error is OperationCanceledException && !startAttempted && cancelled()) reason = "stopped_before_recording";
            else errors.Add(error.GetType().Name + ": " + error.Message);
        }
        finally
        {
            if (session != null && startAttempted)
            {
                session.IntentionalStop = true;
                hostTiming["stop_call_ticks"] = Stopwatch.GetTimestamp();
                try
                {
                    Wait(session.Device.StopRecording(), "StopRecording");
                    hostTiming["stop_return_ticks"] = Stopwatch.GetTimestamp();
                }
                catch (Exception error) { errors.Add("Stop: " + error.Message); }
                try
                {
                    string fault = session.PollFault();
                    if (fault != null && fault != reason) errors.Add(fault);
                }
                catch (Exception error) { errors.Add("Status: " + error.Message); }
            }
            if (sink != null)
            {
                try { sink.Finish(); }
                catch (Exception error) { errors.Add("CSV close: " + error.Message); }
                sink.AddStatistics(report);
                hostTiming["first_written_callback_ticks"] = sink.FirstWrittenTicks;
                hostTiming["last_written_callback_ticks"] = sink.LastWrittenTicks;
                hostTiming["utc_after_capture"] = ReadClockAnchor();
                if (sink.Fault != null && sink.Fault != reason) errors.Add(sink.Fault);
            }
            if (session != null)
            {
                report["device_status_or"] = (int)session.StatusSeen;
                report["device_status_text"] = session.StatusSeen.ToString();
                try { session.Dispose(); }
                catch (Exception error) { errors.Add("Close: " + error.Message); }
            }
        }
        if ((reason == "duration_reached" || reason == "operator_stop") && sink != null && sink.Written >= 2 && errors.Count == 0)
            report["state"] = "complete";
        report["stop_reason"] = reason;
        report["errors"] = errors;
        report["closed_utc"] = DateTime.UtcNow.ToString("O", Invariant);
        SaveJson(directory, "capture.json", report);
        return report;
    }

    private static string CaptureUntil(CsvSink sink, DeviceSession session, double? seconds, Func<bool> cancelled)
    {
        // The packet interval is distinct from the nominal interval between readings.
        double stale = Math.Max(5.0, 10.0 / sink.Rate);
        while (true)
        {
            string fault = sink.Fault ?? session.PollFault();
            if (fault != null) return fault;
            if (cancelled()) return "operator_stop";
            double elapsed = sink.Elapsed;
            if (seconds.HasValue && sink.FirstReceive >= 0 && elapsed - sink.FirstReceive >= seconds.Value) return "duration_reached";
            if (elapsed - Math.Max(sink.LastReceive, 0.0) >= stale) return "no_recent_readings";
            Thread.Sleep(40);
        }
    }

    internal static void SaveJson(string directory, string name, object data)
    {
        // Only the final filename denotes a fully written summary.
        string path = Path.Combine(directory, name);
        string pending = path + ".writing";
        File.WriteAllText(pending, new JavaScriptSerializer().Serialize(data) + "\n", Utf8);
        File.Move(pending, path);
    }

    private static void Print(object data)
    {
        Console.WriteLine(new JavaScriptSerializer().Serialize(data));
    }
}

internal sealed class MonitoredConnector : IQC20Connector
{
    private readonly IQC20Connector inner;
    internal volatile IQC20Proxy Proxy;
    internal volatile bool Locked;
    internal volatile string Fault;

    internal MonitoredConnector(IQC20Connector inner)
    {
        this.inner = inner;
    }

    public async Task<IQC20Proxy> ConnectAsync(EndPoint endpoint, CancellationToken token)
    {
        // The vendor device may attempt to reopen the link. Keep each file to one connection.
        if (Locked) throw new OperationCanceledException("This capture session has ended.");
        IQC20Proxy old = Proxy;
        if (old != null) old.ErrorOccurred -= OnError;
        IQC20Proxy proxy = await inner.ConnectAsync(endpoint, token).ConfigureAwait(false);
        if (Locked || token.IsCancellationRequested)
        {
            var disposable = proxy as IDisposable;
            if (disposable != null) disposable.Dispose();
            throw new OperationCanceledException(token);
        }
        proxy.ErrorOccurred += OnError;
        Proxy = proxy;
        return proxy;
    }

    internal void Detach()
    {
        IQC20Proxy proxy = Proxy;
        if (proxy != null) proxy.ErrorOccurred -= OnError;
    }

    private void OnError(object sender, ErrorEventArgs args)
    {
        if (Locked) Fault = "transport_error: " + args.GetException().Message;
    }
}

internal sealed class StreamReadinessTimer : ITimer
{
    // QC20Ballbar starts its timer after initialization reaches the Connected state.
    // In a live session, suppress that SDK callback entirely, including at stream startup.
    // It sends a separate displacement request and closes the connection if that request times out.
    private volatile bool ready;
    internal bool Ready { get { return ready; } }

    public void Start(TimerCallback callback) { ready = true; }
    public void Start(TimerCallback callback, object state, int dueTime, int period) { ready = true; }
    public bool Change(int dueTime, int period) { return ready; }
    public void Stop() { ready = false; }
}

internal sealed class DeviceSession : IDisposable
{
    private readonly MonitoredConnector connector;
    private readonly StreamReadinessTimer streamReadiness;
    private readonly string serial;
    private readonly bool ble;
    private bool opened;
    private volatile bool unexpectedStop;
    internal volatile bool IntentionalStop;
    internal BallbarStatus StatusSeen;
    internal readonly QC20Ballbar Device;
    internal double? MinimumReadingMm { get; private set; }
    internal double? MaximumReadingMm { get; private set; }

    internal DeviceSession(IQC20Connector connector, BluetoothEndPoint endpoint, string serial, bool ble,
        ISleepInhibitor sleep, bool streamOnly = false)
    {
        this.connector = new MonitoredConnector(connector);
        this.serial = serial;
        this.ble = ble;
        streamReadiness = streamOnly ? new StreamReadinessTimer() : null;
        ITimer timer = streamReadiness == null ? (ITimer)new RealTimer() : streamReadiness;
        Device = new QC20Ballbar(this.connector, endpoint, timer, new RealDelay(), sleep);
        Device.RecordingStopped += OnStopped;
    }

    private static bool Failed(BallbarStatus status)
    {
        return (status & ~(BallbarStatus.LowBattery | BallbarStatus.LowSignal)) != BallbarStatus.None;
    }

    internal void WaitReady(Func<bool> cancelled)
    {
        opened = true;
        Device.Open();
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 30)
        {
            if (cancelled()) throw new OperationCanceledException();
            if (streamReadiness != null && !streamReadiness.Ready)
            {
                Thread.Sleep(40);
                continue;
            }
            var status = CurrentStatus();
            if (Device.DeviceInfo != null && (status & BallbarStatus.Unavailable) == 0)
            {
                if (!string.IsNullOrEmpty(serial) && !string.Equals(Device.DeviceInfo.SerialNumber, serial, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The connected serial number does not match the selected device.");
                var wake = Device.PowerOn();
                CaptureApp.Wait(wake, "PowerOn");
                if (wake.Result && (status & BallbarStatus.PoweredDown) == 0)
                {
                    StatusSeen |= status;
                    if (Failed(status)) throw new IOException("Device status at connection: " + status);
                    CaptureApp.Downsample(Device.SampleRate, "native");
                    connector.Locked = true;
                    ReadMeasurementRange();
                    return;
                }
            }
            Thread.Sleep(40);
        }
        throw new TimeoutException("No ready QC20 connection within 30 seconds.");
    }

    private void ReadMeasurementRange()
    {
        IQC20Proxy proxy = connector.Proxy;
        // QC20Ballbar has already read these bounds while constructing its displacement filter.
        var minimum = proxy.ReadMinimumValue();
        var maximum = proxy.ReadMaximumValue();
        CaptureApp.Wait(Task.WhenAll(minimum, maximum), "Read measurement range");
        double low = minimum.Result, high = maximum.Result;
        if (CaptureApp.Finite(low) && CaptureApp.Finite(high) &&
            low != double.MinValue && high != double.MaxValue && low < high)
        {
            MinimumReadingMm = low;
            MaximumReadingMm = high;
        }
    }

    internal object Describe(double? streamReadingMm = null)
    {
        Version firmware;
        Device.VersionInfo.TryGetValue("Firmware", out firmware);
        double latest = streamReadingMm ?? Device.LatestReading.Value;
        return new {
            model = Device.DeviceInfo.Model, serial = Device.DeviceInfo.SerialNumber,
            bluetooth_mode = ble ? "ble" : "classic", firmware = firmware == null ? null : firmware.ToString(),
            native_sample_rate_hz = Device.SampleRate, status = CurrentStatus().ToString(),
            measurement_min_mm = MinimumReadingMm, measurement_max_mm = MaximumReadingMm,
            latest_reading_mm = CaptureApp.Finite(latest) ? (double?)latest : null,
        };
    }

    private BallbarStatus CurrentStatus()
    {
        // For live sessions the proxy latches status from the streaming encoder messages.
        // QC20Ballbar.LatestReading.Status comes from the disabled single-read timer.
        var status = streamReadiness == null ? Device.LatchedStatus : BallbarStatus.None;
        IQC20Proxy proxy = connector.Proxy;
        if (proxy != null) status |= proxy.LatestStatus;
        return status;
    }

    internal string PollFault()
    {
        var status = CurrentStatus();
        StatusSeen |= status;
        if (connector.Fault != null) return connector.Fault;
        if (unexpectedStop) return "device_stopped_unexpectedly";
        return Failed(status) ? "device_status: " + status : null;
    }

    private void OnStopped(object sender, EventArgs args)
    {
        if (!IntentionalStop) unexpectedStop = true;
    }

    public void Dispose()
    {
        Device.RecordingStopped -= OnStopped;
        connector.Locked = true;
        connector.Detach();
        try { if (opened) Device.Close(); }
        finally { Device.Dispose(); }
    }
}

internal sealed class CsvSink : ChannelFilter
{
    private struct Sample
    {
        internal long Index, Ticks;
        internal double Mm;
    }

    private readonly BlockingCollection<Sample> queue = new BlockingCollection<Sample>(16384);
    private readonly object gate = new object();
    private readonly Task writer;
    private readonly long origin = Stopwatch.GetTimestamp();
    private bool finished;
    private long first = -1, last = -1;
    private long next, dropped, invalid;
    private long atLowerLimit, atUpperLimit;
    private long firstWritten = -1, lastWritten = -1, maxGap;
    private string fault;
    private long written;
    internal long Written { get { return Interlocked.Read(ref written); } }
    internal readonly double Rate;
    private readonly double? minimumMm, maximumMm;

    internal CsvSink(string file, double rate, double? minimumMm, double? maximumMm)
    {
        Rate = rate;
        this.minimumMm = minimumMm;
        this.maximumMm = maximumMm;
        var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536);
        writer = Task.Factory.StartNew(() => Write(stream), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    internal double FirstReceive { get { return Seconds(Interlocked.Read(ref first)); } }
    internal double LastReceive { get { return Seconds(Interlocked.Read(ref last)); } }
    internal double Elapsed { get { return Seconds(Stopwatch.GetTimestamp() - origin); } }
    internal long OriginTicks { get { return origin; } }
    internal long? FirstWrittenTicks { get { return Written == 0 ? (long?)null : origin + firstWritten; } }
    internal long? LastWrittenTicks { get { return Written == 0 ? (long?)null : origin + lastWritten; } }
    internal string Fault { get { return Volatile.Read(ref fault); } }

    private static double Seconds(long ticks)
    {
        return ticks < 0 ? -1 : ticks / (double)Stopwatch.Frequency;
    }

    public override double Process(double value)
    {
        lock (gate)
        {
            if (finished) return value;
            long now = Stopwatch.GetTimestamp() - origin;
            Interlocked.CompareExchange(ref first, now, -1);
            Interlocked.Exchange(ref last, now);
            var sample = new Sample { Index = next++, Ticks = now, Mm = value };
            if (dropped != 0 || !queue.TryAdd(sample))
            {
                dropped++;
                Interlocked.CompareExchange(ref fault, "host_queue_overflow", null);
            }
            if (!CaptureApp.Finite(value))
            {
                invalid++;
                Interlocked.CompareExchange(ref fault, "non_finite_sensor_reading", null);
            }
            else if (minimumMm.HasValue && value <= minimumMm.Value) atLowerLimit++;
            else if (maximumMm.HasValue && value >= maximumMm.Value) atUpperLimit++;
        }
        return value;
    }

    private void Write(FileStream stream)
    {
        try
        {
            using (stream)
            using (var csv = new StreamWriter(stream, new UTF8Encoding(false), 65536, true))
            {
                csv.WriteLine("sample_index,time_s,length_delta_um,host_rx_elapsed_s");
                var flushTimer = Stopwatch.StartNew();
                while (!queue.IsCompleted)
                {
                    Sample sample;
                    if (queue.TryTake(out sample, 100))
                    {
                        csv.WriteLine(string.Format(CaptureApp.Invariant, "{0},{1:R},{2:R},{3:R}",
                            sample.Index, sample.Index / Rate, sample.Mm * 1000.0, Seconds(sample.Ticks)));
                        if (written == 0) firstWritten = sample.Ticks;
                        else maxGap = Math.Max(maxGap, sample.Ticks - lastWritten);
                        lastWritten = sample.Ticks;
                        Interlocked.Increment(ref written);
                    }
                    if (flushTimer.ElapsedMilliseconds >= 1000)
                    {
                        csv.Flush();
                        flushTimer.Restart();
                    }
                }
                csv.Flush();
                stream.Flush(true);
            }
        }
        catch (Exception error)
        {
            Interlocked.CompareExchange(ref fault, "csv_write_error: " + error.Message, null);
        }
    }

    internal void Finish()
    {
        lock (gate)
        {
            finished = true;
            queue.CompleteAdding();
        }
        writer.GetAwaiter().GetResult();
        queue.Dispose();
    }

    internal void AddStatistics(Dictionary<string, object> report)
    {
        report["samples_received"] = next;
        report["samples_written"] = Written;
        report["host_queue_dropped"] = dropped;
        report["non_finite_samples_received"] = invalid;
        report["at_lower_limit_samples_received"] = minimumMm.HasValue ? (object)atLowerLimit : null;
        report["at_upper_limit_samples_received"] = maximumMm.HasValue ? (object)atUpperLimit : null;
        report["range_check"] = !minimumMm.HasValue ? "limits_unavailable" :
                                  atLowerLimit + atUpperLimit > 0 ? "limit_reached" : "no_limit_observed";
        report["nominal_sample_span_s"] = Written == 0 ? (object)null : (Written - 1) / Rate;
        report["first_host_rx_elapsed_s"] = Written == 0 ? (object)null : Seconds(firstWritten);
        report["host_rx_span_s"] = Written == 0 ? (object)null : Seconds(lastWritten - firstWritten);
        report["max_host_rx_gap_s"] = Written < 2 ? (object)null : Seconds(maxGap);
    }
}
