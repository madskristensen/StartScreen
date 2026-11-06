using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

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

        internal static Task LogAsync(this Exception exception,
            [CallerMemberName] string operation = null, [CallerFilePath] string sourceFile = null)
        {
            string detail = exception is OperationCanceledException ? "Canceled." : exception.ToString();
            return WriteAsync(Path.GetFileNameWithoutExtension(sourceFile) ?? Vsix.Name, $"{operation}: {detail}");
        }

        private static async Task WriteAsync(string source, string message)
        {
            try
            {
                await _buffer.WriteAsync(source, message);
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

        internal Task WriteAsync(string source, string message)
        {
            _messages.Enqueue($"[{DateTimeOffset.Now:HH:mm:ss}] [{source}] {message}");
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