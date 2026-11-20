using System.Diagnostics;
using System.IO.MemoryMappedFiles;

namespace QC20daq;

internal sealed record Preview(IReadOnlyList<double?[]> Points, long Count, double? ElapsedS,
    double? LatestUm, double? MinUm, double? MaxUm, double WindowS = 12, bool Stale = false)
{
    internal static readonly Preview Empty = new([], 0, null, null, null, null);
}

internal sealed class LivePreview : IDisposable
{
    // Same layout as the x86 collector. The sequence is odd during a write.
    private const int Capacity = 16_384;
    private const int Header = 32;
    private const int Bins = 180;

    private readonly double _rate;
    private readonly double[] _samples;
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private Preview _plot = Preview.Empty;
    private long _lastRead;
    private int _sequence = -1;

    internal long RecordToken { get; private set; }
    internal long Written { get; private set; }
    internal bool HasContinuousReadings { get; private set; }
    internal Preview Snapshot => _lastRead != 0 && Stopwatch.GetElapsedTime(_lastRead).TotalSeconds > 1
        ? _plot with { Stale = true } : _plot;

    internal LivePreview(string mapping, double rate)
    {
        if (!(rate > 0 && rate <= 1000.001)) throw new InvalidDataException("无效的预览采样率。");
        _rate = rate;
        _samples = new double[Math.Min(Capacity, (int)(12 * rate) + 1)];
        _mapping = MemoryMappedFile.OpenExisting(mapping, MemoryMappedFileRights.Read);
        try { _view = _mapping.CreateViewAccessor(0, Header + Capacity * sizeof(double), MemoryMappedFileAccess.Read); }
        catch { _mapping.Dispose(); throw; }
    }

    internal void Update()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            int sequence = _view.ReadInt32(0);
            if (sequence == _sequence) return;
            if ((sequence & 1) != 0) continue;
            Thread.MemoryBarrier();
            long count = _view.ReadInt64(8);
            long token = _view.ReadInt64(16);
            long written = _view.ReadInt64(24);
            if (count < _plot.Count || written < 0) continue;

            int length = (int)Math.Min(count, _samples.Length);
            if (count != _plot.Count)
            {
                int start = (int)((count - length) % Capacity);
                int first = Math.Min(length, Capacity - start);
                _view.ReadArray(Header + start * sizeof(double), _samples, 0, first);
                if (first < length) _view.ReadArray(Header, _samples, first, length - first);
            }

            Thread.MemoryBarrier();
            if (_view.ReadInt32(0) != sequence) continue;
            _sequence = sequence;
            RecordToken = token;
            Written = written;
            if (count != _plot.Count)
            {
                HasContinuousReadings = _plot.Count > 0;
                _plot = BuildPlot(length, count);
                _lastRead = Stopwatch.GetTimestamp();
            }
            return;
        }
    }

    private Preview BuildPlot(int length, long count)
    {
        var points = new List<double?[]>(Math.Min(5 * Bins, length));
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        Span<int> keep = stackalloc int[5];
        for (int first = 0; first < length;)
        {
            int end = first + 1;
            if (length > Bins)
            {
                int bin = Math.Min(Bins - 1, first * Bins / (length - 1));
                while (end < length && Math.Min(Bins - 1, end * Bins / (length - 1)) == bin) end++;
            }

            int minIndex = -1, maxIndex = -1, missing = -1;
            for (int i = first; i < end; i++)
            {
                double value = _samples[i];
                if (!double.IsFinite(value))
                {
                    if (missing < 0) missing = i;
                    continue;
                }
                min = Math.Min(min, value);
                max = Math.Max(max, value);
                if (minIndex < 0 || value < _samples[minIndex]) minIndex = i;
                if (maxIndex < 0 || value > _samples[maxIndex]) maxIndex = i;
            }

            keep[0] = first;
            keep[1] = end - 1;
            keep[2] = minIndex;
            keep[3] = maxIndex;
            keep[4] = missing;
            keep.Sort();
            int prior = -1;
            foreach (int i in keep)
            {
                if (i < 0 || i == prior) continue;
                points.Add([(count - length + i) / _rate, Micrometres(_samples[i])]);
                prior = i;
            }
            first = end;
        }

        return new(points, count, (count - 1) / _rate, Micrometres(_samples[length - 1]), Micrometres(min), Micrometres(max));
    }

    private static double? Micrometres(double value)
    {
        double converted = value * 1000;
        return double.IsFinite(converted) ? converted : null;
    }

    public void Dispose()
    {
        _view.Dispose();
        _mapping.Dispose();
    }
}
