using System.Diagnostics;

namespace StartScreen.Helpers
{
    internal static class StartupDiagnostics
    {
        private static readonly object _syncRoot = new object();
        private static Stopwatch _stopwatch;

        [Conditional("DEBUG")]
        public static void Start()
        {
            lock (_syncRoot)
            {
                _stopwatch = Stopwatch.StartNew();
                Trace.WriteLine("[StartScreen startup] 0 ms: startup began");
            }
        }

        [Conditional("DEBUG")]
        public static void Mark(string milestone)
        {
            lock (_syncRoot)
            {
                if (_stopwatch == null || _stopwatch.Elapsed > TimeSpan.FromMinutes(2))
                    return;

                Trace.WriteLine(
                    $"[StartScreen startup] {_stopwatch.Elapsed.TotalMilliseconds:F0} ms: {milestone}");
            }
        }
    }
}
