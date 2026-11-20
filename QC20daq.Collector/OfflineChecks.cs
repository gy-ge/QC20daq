using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Renishaw.Calibration;
using Renishaw.Calibration.Ballbar.Api;
using Renishaw.Calibration.Devices.QC;
using Renishaw.IO;
using Renishaw.IO.Bluetooth;
using Renishaw.IO.Bluetooth.Classic;
using Renishaw.IO.Bluetooth.LowEnergy;
using Renishaw.IO.ChannelFilters;

internal static class OfflineChecks
{
    private static readonly BluetoothLowEnergyEndPoint SimulatedEndpoint =
        new BluetoothLowEnergyEndPoint(new BluetoothAddress(1UL), new[] { Guid.Empty });

    internal static DeviceSession OpenPreviewDevice(bool streamOnly)
    {
        return new DeviceSession(new SimulatedQc20(0, "preview", streamOnly), SimulatedEndpoint,
            "SIM-QC20", true, new NoSleepChange(), streamOnly);
    }

    internal static Dictionary<string, object> Run(string work)
    {
        var cases = new Dictionary<string, object>();
        cases["native_1000"] = CheckCapture(work, "native_1000", "native", 12346, "normal", 12347, "duration_reached", 1);
        cases["divided_250"] = CheckCapture(work, "divided_250", "250", 237, "operator", 238, "operator_stop", 4);
        cases["non_finite"] = CheckCapture(work, "non_finite", "native", 17, "non_finite", 18, "non_finite_sensor_reading", 1);
        cases["overrun"] = CheckCapture(work, "overrun", "100", 37, "overrun", 37, "device_status: BufferOverrun, Error", 10);
        cases["transport_error"] = CheckCapture(work, "transport_error", "native", 37, "transport_error", 37, "transport_error: simulated packet sequence error", 1);
        cases["prior_live_single_read_timeout"] = CheckPriorLiveTimeout(work);
        cases["live_single_read_isolation"] = CheckLiveStream(work, "preview", "duration_reached");
        cases["live_stalled_before_duration"] = CheckLiveStream(work, "preview_stall", "no_recent_readings");
        cases["live_transport_fault"] = CheckLiveStream(work, "preview_transport", "transport_error: simulated packet sequence error");
        bool rejected = false;
        try { CaptureApp.Downsample(1000, "333"); }
        catch (ArgumentException) { rejected = true; }
        Require(rejected, "unsupported rate was accepted");
        Require(CaptureApp.Downsample(250, "native") == 1, "native rate should come from device configuration");
        return new Dictionary<string, object> {
            {"schema", "qc20_capture_offline_check_v1"}, {"passed", true}, {"hardware_contacted", false},
            {"libraries", CaptureApp.Inspect()}, {"cases", cases}, {"unsupported_rate_rejected", rejected},
            {"legacy_api_stop_buffer_probe", LegacyApiTail()},
            {"basis", "Synthetic readings through installed QC20Ballbar, real CSV recorder. No Bluetooth link or hardware endurance test."},
        };
    }

    private static object CheckPriorLiveTimeout(string work)
    {
        string root = Path.Combine(work, "prior_live_single_read_timeout");
        string control = Path.Combine(root, "live_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(control);
        CaptureApp.SaveJson(control, "provenance.json", new { });
        var fake = new SimulatedQc20(0, "preview", true);
        var clock = Stopwatch.StartNew();
        int exit = LiveCapture.Run(control, root, "native",
            () => new DeviceSession(fake, SimulatedEndpoint, "SIM-QC20", true, new NoSleepChange()),
            () => clock.Elapsed.TotalSeconds > 10, "simulated");
        string ended = File.ReadAllText(Path.Combine(control, "ended.json"));
        Require(exit == 2 && fake.SingleReadTimeouts > 0 && ended.Contains("device_stopped_unexpectedly"),
            "the vendor periodic read did not reproduce the previous failure");
        return new { exit_code = exit, single_read_timeouts = fake.SingleReadTimeouts, reproduced_stop = true };
    }

    private static object CheckLiveStream(string work, string mode, string expectedStop)
    {
        string root = Path.Combine(work, "live_" + mode);
        string control = Path.Combine(root, "live_" + Guid.NewGuid().ToString("N"));
        const string id = "web_20200101_000000";
        string capture = Path.Combine(root, id);
        string summary = Path.Combine(capture, "capture.json");
        Directory.CreateDirectory(control);
        Directory.CreateDirectory(capture);
        CaptureApp.SaveJson(control, "provenance.json", new { });
        CaptureApp.SaveJson(control, "record.json", new LiveRecordRequest { id = id, token = 1, seconds = 1.3 });

        var fake = new SimulatedQc20(0, mode, true);
        var clock = Stopwatch.StartNew();
        long samplesAtSave = -1;
        bool timedOut = false;
        int exit = LiveCapture.Run(control, root, "native",
            () => new DeviceSession(fake, SimulatedEndpoint, "SIM-QC20", true, new NoSleepChange(), streamOnly: true),
            () =>
            {
                timedOut = clock.Elapsed.TotalSeconds > 14;
                if (timedOut) return true;
                if (mode != "preview" || !File.Exists(summary)) return false;
                if (samplesAtSave < 0) samplesAtSave = fake.StreamedSamples;
                return fake.StreamedSamples - samplesAtSave >= 300;
            }, "simulated");
        bool staysLive = mode == "preview";
        Require(!timedOut && exit == (staysLive ? 0 : 2), mode + ": stream exit state");
        Require(fake.SingleReads == 0, mode + ": out-of-band displacement read occurred");
        Require(fake.ConnectCount == 1, mode + ": stream reconnected");
        var result = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(summary));
        long samples = Convert.ToInt64(result["samples_written"]);
        Require((string)result["state"] == (staysLive ? "complete" : "interrupted") &&
                (string)result["stop_reason"] == expectedStop, mode + ": incorrect final status");
        Require(samples > 100 && samples == Convert.ToInt64(result["samples_received"]) &&
                samples == File.ReadLines(Path.Combine(capture, "ballbar_20200101_000000.csv")).LongCount() - 1 &&
                Convert.ToInt64(result["host_queue_dropped"]) == 0, mode + ": captured samples were lost");
        if (staysLive)
            Require(samplesAtSave >= 0 && fake.StreamedSamples - samplesAtSave >= 300,
                mode + ": data stream did not continue after saving");
        return new {
            result,
            single_displacement_reads = fake.SingleReads,
            callbacks_after_save = staysLive ? (long?)(fake.StreamedSamples - samplesAtSave) : null,
        };
    }

    private static Dictionary<string, object> CheckCapture(string work, string name, string rate,
        int samples, string mode, int expectedCount, string stopReason, int factor)
    {
        string directory = Path.Combine(work, name);
        Directory.CreateDirectory(directory);
        var fake = new SimulatedQc20(samples, mode);
        var address = new BluetoothAddress(1UL);
        bool ble = mode == "normal";
        BluetoothEndPoint endpoint = ble ? (BluetoothEndPoint)new BluetoothLowEnergyEndPoint(address, new[] { Guid.Empty })
                                         : new BluetoothClassicEndPoint(address, BluetoothGuids.SerialPortServiceClassGuid);
        string csv = Path.Combine(directory, "ballbar.csv");
        bool wasWrittenBeforeStop = false;
        var report = CaptureApp.Record(directory, mode == "normal" ? 1.25 : 0.12, rate,
            () => new DeviceSession(fake, endpoint, "SIM-QC20", ble, new NoSleepChange()),
            () =>
            {
                if (mode == "normal" && fake.IsEncoderStreaming)
                    wasWrittenBeforeStop |= new FileInfo(csv).Length > 8192L;
                return mode == "operator" && fake.IsEncoderStreaming;
            }, "simulated");
        Require(fake.ConnectCount == 1, name + ": unexpected reconnect");
        Require(fake.Downsampling == factor && !fake.Averaging, name + ": unexpected request to device");
        string observedReason = (string)report["stop_reason"];
        Require(observedReason == stopReason, name + ": stop reason " + observedReason);
        bool success = mode == "normal" || mode == "operator";
        Require((string)report["state"] == (success ? "complete" : "interrupted"), name + ": capture state");
        Require(success == fake.StopTailDelivered, name + ": incorrect tail condition");
        Require(!(bool)report["sample_rate_verified"] && !(bool)report["sample_continuity_verified"], name + ": verification claims");
        Require((long)report["samples_written"] == expectedCount, name + ": unexpected number of written samples");
        var rows = File.ReadAllLines(csv);
        Require(rows.Length == expectedCount + 1, name + ": unexpected number of CSV rows");
        Require(rows[0] == "sample_index,time_s,length_delta_um,host_rx_elapsed_s", name + ": CSV schema");
        double previousHost = -1;
        double lastValue = 0;
        double actual = 1000.0 / factor;
        for (int i = 0; i < expectedCount; i++)
        {
            string[] row = rows[i + 1].Split(',');
            Require(int.Parse(row[0], CaptureApp.Invariant) == i, name + ": index order");
            Require(Math.Abs(double.Parse(row[1], CaptureApp.Invariant) - i / actual) < 1e-10, name + ": sample spacing");
            double host = double.Parse(row[3], CaptureApp.Invariant);
            Require(host >= previousHost, name + ": nonmonotonic host timestamp");
            previousHost = host;
            lastValue = double.Parse(row[2], CaptureApp.Invariant);
        }
        var clock = (Dictionary<string, object>)report["host_timing"];
        long origin = (long)clock["rx_elapsed_origin_ticks"];
        long frequency = (long)clock["counter_frequency_hz"];
        long firstTick = (long)clock["first_written_callback_ticks"];
        long lastTick = (long)clock["last_written_callback_ticks"];
        double firstCsvHost = double.Parse(rows[1].Split(',')[3], CaptureApp.Invariant);
        Require(frequency > 0 && origin <= (long)clock["start_call_ticks"], name + ": counter origin");
        Require((long)clock["start_call_ticks"] <= firstTick && firstTick <= lastTick, name + ": callback tick ordering");
        Require((long)clock["start_call_ticks"] <= (long)clock["start_return_ticks"] &&
                (long)clock["start_return_ticks"] <= (long)clock["stop_call_ticks"] &&
                (long)clock["stop_call_ticks"] <= (long)clock["stop_return_ticks"], name + ": software event tick ordering");
        Require((long)Math.Round(firstCsvHost * frequency) == firstTick - origin &&
                (long)Math.Round(previousHost * frequency) == lastTick - origin, name + ": CSV callback tick round trip");
        var beforeUtc = (Dictionary<string, object>)clock["utc_before_start"];
        var afterUtc = (Dictionary<string, object>)clock["utc_after_capture"];
        Require(origin <= (long)beforeUtc["counter_before_ticks"] &&
                (long)beforeUtc["counter_before_ticks"] <= (long)beforeUtc["counter_after_ticks"] &&
                (long)beforeUtc["counter_after_ticks"] <= (long)clock["start_call_ticks"], name + ": clock pair before capture");
        Require(lastTick <= (long)afterUtc["counter_before_ticks"] &&
                (long)afterUtc["counter_before_ticks"] <= (long)afterUtc["counter_after_ticks"], name + ": clock pair after capture");
        foreach (var anchor in new[] { beforeUtc, afterUtc })
        {
            var utc = DateTime.FromFileTimeUtc(long.Parse((string)anchor["utc_filetime_100ns"], CaptureApp.Invariant));
            Require(Math.Abs((DateTime.UtcNow - utc).TotalSeconds) < 60.0, name + ": host UTC clock pair");
        }
        if (success) Require(Math.Abs(lastValue - 42.0) < 1e-9, name + ": final real reading was not retained");
        if (mode == "non_finite") Require(double.IsNaN(lastValue), name + ": invalid final point disappeared");
        if (mode == "normal")
        {
            Require(wasWrittenBeforeStop, name + ": CSV was not written during recording");
            report["csv_written_before_stop"] = wasWrittenBeforeStop;
            Require((long)report["at_lower_limit_samples_received"] == 1 &&
                    (long)report["at_upper_limit_samples_received"] == 1 &&
                    (string)report["range_check"] == "limit_reached", name + ": range limit count");
        }
        report["tested_final_csv_value_um"] = CaptureApp.Finite(lastValue) ? (object)lastValue : "NaN";
        return report;
    }

    private static object LegacyApiTail()
    {
        var assembly = typeof(BallbarDevice).Assembly;
        var type = assembly.GetType("Renishaw.Calibration.Ballbar.Api.ExpandableBufferingFilter");
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        FieldInfo ending = type == null ? null : type.GetField("m_isEnding", flags);
        if (ending == null) return new { available = false };
        using (var probe = (IDisposable)Activator.CreateInstance(type, new object[] { 8 }))
        {
            var filter = (ChannelFilter)probe;
            type.GetMethod("BeginBuffering").Invoke(probe, null);
            for (int i = 1; i <= 3; i++) filter.Process(i);
            // Exercise the ending branch without an instrument feeding a concurrent sample.
            ending.SetValue(probe, true);
            filter.Process(4.0);
            var readings = new double[(int)type.GetProperty("Count").GetValue(probe, null)];
            int count = (int)type.GetMethod("ReadBuffer").Invoke(probe, new object[] { readings, 0, readings.Length });
            return new { submitted = 4, returned = count, values = readings, method = "offline stop-flush branch" };
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed class NoSleepChange : ISleepInhibitor
    {
        public void Enable() { }
        public void Disable() { }
    }

    private sealed class SimulatedQc20 : IQC20Proxy, IQC20Connector, IDisposable
    {
        private readonly int count;
        private readonly string mode;
        private readonly bool failSingleReadInStream;
        private ChannelFilter filter;
        private CancellationTokenSource previewStop;
        private Task previewSource;
        internal int ConnectCount;
        internal int Downsampling;
        internal bool Averaging;
        internal bool StopTailDelivered;
        private long streamedSamples;
        internal long StreamedSamples { get { return Interlocked.Read(ref streamedSamples); } }
        internal int SingleReads;
        internal int SingleReadTimeouts;
        public double SampleRate { get { return 1000.0; } }
        public bool IsEncoderStreaming { get; private set; }
        public BallbarStatus LatestStatus { get; private set; }
        public event EventHandler<ErrorEventArgs> ErrorOccurred = delegate { };

        internal SimulatedQc20(int count, string mode, bool failSingleReadInStream = false)
        {
            this.count = count;
            this.mode = mode;
            this.failSingleReadInStream = failSingleReadInStream;
        }

        public Task<IQC20Proxy> ConnectAsync(EndPoint endpoint, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ConnectCount++;
            return Task.FromResult<IQC20Proxy>(this);
        }

        public Task StartStreaming(ChannelFilter destination, int downsamplingFactor, bool averagingOn)
        {
            filter = destination;
            Downsampling = downsamplingFactor;
            Averaging = averagingOn;
            IsEncoderStreaming = true;
            if (mode.StartsWith("preview", StringComparison.Ordinal))
            {
                previewStop = new CancellationTokenSource();
                var token = previewStop.Token;
                previewSource = Task.Run(() => ProducePreview(1000.0 / downsamplingFactor, token));
                return Task.CompletedTask;
            }
            if (mode == "normal")
            {
                filter.Process(-1200);
                filter.Process(1200);
            }
            int sent = mode == "normal" ? 2 : 0;
            for (int i = sent; i < count; i++) filter.Process((i % 100) - 50);
            if (mode == "non_finite") filter.Process(double.NaN);
            if (mode == "overrun") LatestStatus = BallbarStatus.BufferOverrun | BallbarStatus.Error;
            if (mode == "transport_error") ErrorOccurred(this, new ErrorEventArgs(new InvalidDataException("simulated packet sequence error")));
            return Task.CompletedTask;
        }

        public Task StopStreaming()
        {
            if (previewSource != null) return StopPreview();
            if (mode == "normal" || mode == "operator")
            {
                filter.Process(42);
                StopTailDelivered = true;
            }
            IsEncoderStreaming = false;
            return Task.CompletedTask;
        }

        private async Task ProducePreview(double rate, CancellationToken token)
        {
            var clock = Stopwatch.StartNew();
            long sent = 0;
            long last = mode == "preview" ? long.MaxValue : (long)(0.65 * rate);
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(50, token).ConfigureAwait(false);
                    long next = Math.Min((long)(clock.Elapsed.TotalSeconds * rate), last);
                    while (sent < next && !token.IsCancellationRequested)
                    {
                        double t = sent++ / rate;
                        filter.Process(25 * Math.Sin(2 * Math.PI * 0.75 * t) +
                                       5 * Math.Sin(2 * Math.PI * 3.2 * t) +
                                       7 * Math.Sin(2 * Math.PI * 0.15 * t));
                    }
                    Interlocked.Exchange(ref streamedSamples, sent);
                    if (mode == "preview_transport" && sent == last)
                    {
                        ErrorOccurred(this, new ErrorEventArgs(new InvalidDataException("simulated packet sequence error")));
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        private async Task StopPreview()
        {
            previewStop.Cancel();
            await previewSource.ConfigureAwait(false);
            IsEncoderStreaming = false;
        }

        public Task<string> ReadModel() { return Task.FromResult("QC20 simulated"); }
        public Task<string> ReadSerialNumber() { return Task.FromResult("SIM-QC20"); }
        public Task<QC20EncoderReading> ReadRawDisplacement()
        {
            Interlocked.Increment(ref SingleReads);
            if (failSingleReadInStream && IsEncoderStreaming)
            {
                Interlocked.Increment(ref SingleReadTimeouts);
                return Task.FromException<QC20EncoderReading>(new TimeoutException("simulated single displacement request timeout"));
            }
            return Task.FromResult(new QC20EncoderReading(0.0, BallbarStatus.None));
        }
        public Task<double> ReadNullOffset() { return Task.FromResult(0.0); }
        public Task<double> ReadScaleFactor() { return Task.FromResult(0.001); }
        public Task<double> ReadMinimumValue() { return Task.FromResult(-1.0); }
        public Task<double> ReadMaximumValue() { return Task.FromResult(1.0); }
        public Task<IEnumerable<double>> ReadCoefficients() { return Task.FromResult<IEnumerable<double>>(new[] { 0.0, 1.0 }); }
        public Task<DateTime> ReadCalibrationDate() { return Task.FromResult(new DateTime(2026, 1, 1)); }
        public Task InitialiseEncoderSensorReadingType() { return Task.CompletedTask; }
        public Task<QueryInfo> Query() { return Task.FromResult(new QueryInfo()); }
        public Task<VersionInfoDictionary> ReadVersionInfo()
        {
            return Task.FromResult(new VersionInfoDictionary { { "Firmware", new Version(1, 0) } });
        }
        public Task<string> ReadCalibrationOrganization() { return Task.FromResult("simulated"); }
        public Task<string> ReadCertificateNumber() { return Task.FromResult("simulated"); }
        public Task<double> ReadExpansionCoefficient() { return Task.FromResult(0.0); }
        public Task<double> ReadBoardTemperature() { return Task.FromResult(20.0); }
        public Task Sleep() { return Task.CompletedTask; }
        public Task<double> ReadMaxSamplingRate() { return Task.FromResult(1000.0); }
        public Task<double> ReadTemperatureSensorScaleFactor() { return Task.FromResult(1.0); }
        public Task<double> ReadTemperatureSensorMaxSamplingRate() { return Task.FromResult(10.0); }
        public Task<double> ReadTemperatureSensorAveragingResponseTime() { return Task.FromResult(1.0); }
        public Task StartTemperatureStream(ChannelFilter temperatureFilter, int downSamplingFactor, int transmissionInterval) { return Task.CompletedTask; }
        public Task StopTemperatureStream() { return Task.CompletedTask; }
        public void Dispose()
        {
            IsEncoderStreaming = false;
            if (previewStop != null)
            {
                previewStop.Cancel();
                previewStop.Dispose();
            }
        }
    }
}
