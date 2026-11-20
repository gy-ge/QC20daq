using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace QC20daq;

internal sealed record TraceDevice(string Model, string Serial, string BluetoothMode);

internal sealed record TraceSelection(TraceDevice Device, string Address, Guid Service)
{
    internal static TraceSelection? Read()
    {
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Renishaw_plc");
        if (!Directory.Exists(local)) return null;

        var candidates = new Dictionary<(string Address, Guid Service, bool Ble), TraceSelection>();
        foreach (string install in Directory.EnumerateDirectories(local, "Holman.exe_Url_*"))
        foreach (string version in Directory.EnumerateDirectories(install))
        {
            try
            {
                using var input = File.OpenRead(Path.Combine(version, "user.config"));
                var section = XDocument.Load(input).Root?.Element("userSettings")
                    ?.Element("Renishaw.Calibration.Holman.Properties.Settings");
                if (section is null) continue;

                string Setting(string name) => section.Elements("setting")
                    .LastOrDefault(e => (string?)e.Attribute("name") == name)?.Element("value")?.Value.Trim() ?? "";

                string address = Setting("BallbarAddress").ToUpperInvariant();
                if (!Regex.IsMatch(address, @"\A(?:[0-9A-F]{2}:){5}[0-9A-F]{2}\z") ||
                    !Guid.TryParse(Setting("ServiceGuid"), out Guid service) ||
                    !bool.TryParse(Setting("IsBle"), out bool ble)) continue;

                var device = new TraceDevice(Setting("BallbarModel"), Setting("BallbarSerialNumber"), ble ? "ble" : "classic");
                candidates[(address, service, ble)] = new(device, address, service);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (XmlException) { }
        }

        return candidates.Count == 1 ? candidates.Values.First() : null;
    }
}

internal sealed record WorkerJob(string Id, string Kind, string? Source, string? Target, string? RequestedRate,
    int Pid = 0, long Identity = 0);

internal sealed class CollectorProcess(AppOptions options)
{
    private static readonly string[] Libraries =
    [
        "Renishaw.Calibration.Ballbar.Api.dll",
        "Renishaw.Calibration.Ballbar.dll",
        "Renishaw.Calibration.Devices.dll",
        "Renishaw.Calibration.dll",
        "Renishaw.IO.dll",
        "Renishaw.IO.Bluetooth.dll",
        "Renishaw.IO.SimpleSensorInterface.dll",
        "Renishaw.Net.dll",
        "Microsoft.VisualStudio.Validation.dll",
    ];

    internal string Journal => Path.Combine(options.Control, "last_job.json");
    internal string LogPath(string id) => Path.Combine(options.Logs, $"qc20daq_worker_{id}.log");

    internal void WriteProvenance(string control)
    {
        var files = Libraries.Select(file => Path.Combine(options.Install, file)).Append(options.Collector)
            .Select(file =>
            {
                using var input = File.OpenRead(file);
                return new { File = file, Sha256 = Convert.ToHexStringLower(SHA256.HashData(input)) };
            }).ToArray();
        Protocol.Write(Path.Combine(control, "provenance.json"), files);
    }

    internal (WorkerJob Job, Process Process) Launch(WorkerJob job, IEnumerable<string> args)
    {
        string permit = Path.Combine(options.Control, job.Id + ".permit");
        var start = new ProcessStartInfo(options.Collector)
        {
            WorkingDirectory = options.Control,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["PATH"] = options.Install + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        string temp = Path.Combine(options.Rotary, "temp");
        start.Environment["TEMP"] = temp;
        start.Environment["TMP"] = temp;
        foreach (var argument in new[] { "--web-worker", LogPath(job.Id), permit, options.Install }.Concat(args))
            start.ArgumentList.Add(argument);

        var process = Process.Start(start) ?? throw new IOException("无法启动采集进程。");
        try
        {
            process.PriorityClass = ProcessPriorityClass.Normal;
            job = job with { Pid = process.Id, Identity = process.StartTime.ToUniversalTime().ToFileTimeUtc() };
            Protocol.Write(Journal, job);
            Protocol.Signal(permit);
            return (job, process);
        }
        catch
        {
            // No permit means the child cannot open the device.
            process.Dispose();
            throw;
        }
    }

    internal static Process? Resume(WorkerJob job)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(job.Pid);
            if (!process.HasExited && process.StartTime.ToUniversalTime().ToFileTimeUtc() == job.Identity)
                return process;
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        process?.Dispose();
        return null;
    }

    internal string[] Tail(string id)
    {
        try
        {
            using var file = new FileStream(LogPath(id), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            file.Position = Math.Max(0, file.Length - 16_384);
            using var reader = new StreamReader(file);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (IOException) { return []; }
    }

    internal JsonElement? LastResult(string id, JsonValueKind expected)
    {
        foreach (string line in Tail(id).Reverse())
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind == expected) return doc.RootElement.Clone();
            }
            catch (JsonException) { }
        }
        return null;
    }
}
