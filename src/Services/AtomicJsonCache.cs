using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

namespace StartScreen.Services
{
    /// <summary>Bounded JSON snapshots, published only after a complete write.</summary>
    internal static class AtomicJsonCache
    {
        internal const int MaxBytes = 4 * 1024 * 1024;
        private static readonly SemaphoreSlim _writeGate = new(1, 1);

        internal static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken = default, JsonSerializerOptions options = null)
        {
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path))
                return default;

            using var stream = OpenRead(path);
            var bytes = new byte[stream.Length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var count = await stream.ReadAsync(bytes, offset, bytes.Length - offset, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                    throw new EndOfStreamException("The cache snapshot was truncated.");
                offset += count;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var value = JsonSerializer.Deserialize<T>(bytes, options);
            cancellationToken.ThrowIfCancellationRequested();
            return value;
        }

        internal static T Read<T>(string path, JsonSerializerOptions options = null)
        {
            if (!File.Exists(path))
                return default;
            using var stream = OpenRead(path);
            using var reader = new BinaryReader(stream);
            var bytes = reader.ReadBytes((int)stream.Length);
            if (bytes.Length != stream.Length)
                throw new EndOfStreamException("The cache snapshot was truncated.");
            return JsonSerializer.Deserialize<T>(bytes, options);
        }

        private static FileStream OpenRead(string path)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, useAsync: true);
            if (stream.Length <= MaxBytes)
                return stream;
            stream.Dispose();
            throw new InvalidDataException("The cache snapshot is too large.");
        }

        internal static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken = default, JsonSerializerOptions options = null)
        {
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            string temporaryFile = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = Serialize(value, options);
                temporaryFile = CreateTemporaryPath(path);
                using (var stream = new FileStream(temporaryFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                Publish(temporaryFile, path);
            }
            finally
            {
                try
                {
                    await CleanupAsync(temporaryFile).ConfigureAwait(false);
                }
                finally
                {
                    _writeGate.Release();
                }
            }
        }

        // Retained for existing synchronous consumers. Refresh paths use WriteAsync.
        internal static void Write<T>(string path, T value, JsonSerializerOptions options = null)
        {
            _writeGate.Wait();
            string temporaryFile = null;
            try
            {
                var bytes = Serialize(value, options);
                temporaryFile = CreateTemporaryPath(path);
                using (var stream = new FileStream(temporaryFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    stream.Write(bytes, 0, bytes.Length);
                Publish(temporaryFile, path);
            }
            finally
            {
                try
                {
                    if (temporaryFile != null && File.Exists(temporaryFile))
                        File.Delete(temporaryFile);
                }
                catch (Exception ex)
                {
                    ex.LogAsync().FireAndForget();
                }
                finally
                {
                    _writeGate.Release();
                }
            }
        }

        private static byte[] Serialize<T>(T value, JsonSerializerOptions options)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, options);
            if (bytes.Length > MaxBytes)
                throw new InvalidDataException("The cache snapshot is too large.");
            return bytes;
        }

        private static string CreateTemporaryPath(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            return path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        }

        private static void Publish(string temporaryFile, string path)
        {
            if (File.Exists(path))
                File.Replace(temporaryFile, path, null);
            else
                File.Move(temporaryFile, path);
        }

        private static async Task CleanupAsync(string temporaryFile)
        {
            try
            {
                if (temporaryFile != null && File.Exists(temporaryFile))
                    File.Delete(temporaryFile);
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }
    }
}