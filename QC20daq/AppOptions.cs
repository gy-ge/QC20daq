using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QC20daq;

internal sealed record AppOptions(int Port, string Runtime, string Install, bool OpenBrowser)
{
    internal string Address => $"http://127.0.0.1:{Port}";
    internal string Rotary => Path.Combine(Runtime, "rotary");
    internal string Control => Path.Combine(Rotary, "qc20daq");
    internal string Captures => Path.Combine(AppContext.BaseDirectory, "data");
    internal string Logs => Path.Combine(Rotary, "logs");
    internal string Collector => Path.Combine(AppContext.BaseDirectory, "collector", "Qc20Capture.exe");
    internal string InstanceKey => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Runtime.ToUpperInvariant())));

    internal static AppOptions Parse(string[] args)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string runtime = Environment.GetEnvironmentVariable("DYNAMIC_ERROR_RUNTIME")
            ?? Path.Combine(local, "Codex", "Runtimes", "dynamic-error");
        string install = @"C:\Program Files (x86)\Renishaw Ballbar Trace";
        int port = 8765;
        bool openBrowser = true;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port":
                    if (!int.TryParse(Next(args, ref i), NumberStyles.None, CultureInfo.InvariantCulture, out port)
                        || port is < 1 or > 65535)
                        throw new ArgumentException("端口须为 1 到 65535。");
                    break;
                case "--runtime": runtime = Next(args, ref i); break;
                case "--install": install = Next(args, ref i); break;
                case "--no-browser": openBrowser = false; break;
                default: throw new ArgumentException($"未知的启动参数 {args[i]}。");
            }
        }

        return new(port, Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtime)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(install)), openBrowser);
    }

    private static string Next(string[] args, ref int index)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            throw new ArgumentException("启动参数缺少值。");
        return args[index];
    }

    internal void Log(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(Logs, "qc20daq_host.log"),
                $"{DateTimeOffset.Now:O} [{Environment.ProcessId}] {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class ApiError(string message, int status = 400) : Exception(message)
{
    internal int Status { get; } = status;
}

internal static class Protocol
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    internal static void Write<T>(string path, T value)
    {
        string temp = path + ".writing";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(value, Json));
        File.Move(temp, path, overwrite: true);
    }

    internal static JsonElement? Read(string path)
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(input);
            return document.RootElement.Clone();
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    internal static JsonElement Get(this JsonElement? value, string name) =>
        value is { ValueKind: JsonValueKind.Object } json && json.TryGetProperty(name, out var property) ? property : default;

    internal static string? Text(this JsonElement? value, string name)
    {
        var property = value.Get(name);
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    internal static double? Number(this JsonElement? value, string name)
    {
        var property = value.Get(name);
        return property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number) && double.IsFinite(number)
            ? number : null;
    }

    internal static long Integer(this JsonElement? value, string name)
    {
        var property = value.Get(name);
        return property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number) ? number : 0;
    }

    internal static void Signal(string path)
    {
        using var signal = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
    }
}
