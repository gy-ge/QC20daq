using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

internal static class WorkerEntry
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("The collector requires a Trace installation directory and command.");
            return 1;
        }
        if (args[0] != "--web-worker") return CaptureApp.Main(args);
        if (args.Length < 5) return 1;

        // The worker owns the log handle, even when the Web host exits.
        using (var stream = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        using (var log = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
        {
            Console.SetOut(log);
            Console.SetError(log);
            var waiting = Stopwatch.StartNew();
            try
            {
                // The parent persists PID and creation time before allowing device access.
                while (!File.Exists(args[2]))
                {
                    if (waiting.Elapsed >= TimeSpan.FromSeconds(15))
                        throw new TimeoutException("The Web service did not register the collector before startup.");
                    Thread.Sleep(50);
                }
                File.Delete(args[2]);
                return CaptureApp.Main(args.Skip(3).ToArray());
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
                return 1;
            }
        }
    }
}
