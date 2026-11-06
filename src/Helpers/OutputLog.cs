using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using StartScreen.Models;

namespace StartScreen
{
    internal static class OutputLog
    {
        private static readonly OutputLogBuffer _buffer = new OutputLogBuffer();

        internal static async Task InitializeAsync()
        {
            try
            {
                OutputWindowPane pane = await VS.Windows.CreateOutputWindowPaneAsync(Vsix.Name);
                await _buffer.InitializeAsync(pane.WriteLineAsync);
                await WriteAsync("Startup", $"{Vsix.Name} {Vsix.Version} initialized.");
            }
            catch (Exception ex)
            {
                await System.ExceptionExtensions.LogAsync(ex);
            }
        }

        internal static void Info(string source, string message)
        {
            WriteAsync(source, message).Forget();
        }

        internal static void Refresh(string source, string message, SectionRefreshState state)
        {
            WriteAsync(source, FormatRefreshMessage(message, state), suppressRepeated: true).Forget();
        }

        internal static string FormatRefreshMessage(string message, SectionRefreshState state)
        {
            var details = new List<string>();
            if (state.IsCached)
                details.Add("cached content");
            if (state.IsStale)
                details.Add("may be out of date");
            if (state.IsPartial)
                details.Add("some sources unavailable");
            if (state.IsUnavailable)
                details.Add("unavailable");
            return details.Count == 0 ? message : $"{message} ({string.Join(", ", details)})";
        }

        private static async Task WriteAsync(string source, string message, bool suppressRepeated = false)
        {
            try
            {
                await _buffer.WriteAsync(source, message, suppressRepeated);
            }
            catch (Exception ex)
            {
                await System.ExceptionExtensions.LogAsync(ex, $"{Vsix.Name} output: {source}: {message}");
            }
        }
    }

    internal sealed class OutputLogBuffer
    {
        private readonly ConcurrentQueue<string> _messages = new ConcurrentQueue<string>();
        private readonly Dictionary<string, string> _lastStatusMessages = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private Func<string, Task> _writeLineAsync;

        internal async Task InitializeAsync(Func<string, Task> writeLineAsync)
        {
            if (writeLineAsync == null)
                throw new ArgumentNullException(nameof(writeLineAsync));
            await _gate.WaitAsync();
            try
            {
                _writeLineAsync = writeLineAsync;
            }
            finally
            {
                _gate.Release();
            }
            await FlushAsync();
        }

        internal Task WriteAsync(string source, string message, bool suppressRepeated = false)
        {
            lock (_messages)
            {
                if (!suppressRepeated || !_lastStatusMessages.TryGetValue(source, out var previous) || previous != message)
                    _messages.Enqueue($"[{DateTimeOffset.Now:HH:mm:ss}] [{source}] {message}");
                if (suppressRepeated)
                    _lastStatusMessages[source] = message;
            }
            return FlushAsync();
        }

        private async Task FlushAsync()
        {
            if (_writeLineAsync == null)
                return;
            await _gate.WaitAsync();
            try
            {
                while (_messages.TryPeek(out string message))
                {
                    await _writeLineAsync(message);
                    _messages.TryDequeue(out _);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}