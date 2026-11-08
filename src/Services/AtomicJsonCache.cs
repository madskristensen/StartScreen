using System.Collections.Generic;
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
        private static readonly Dictionary<string, AccessGate> _accessGates = new(StringComparer.OrdinalIgnoreCase);

        private static AccessLease GetAccess(string path)
        {
            path = Path.GetFullPath(path);
            lock (_accessGates)
            {
                if (!_accessGates.TryGetValue(path, out var access))
                    _accessGates.Add(path, access = new AccessGate());
                access.Users++;
                return new AccessLease(path, access);
            }
        }

        private sealed class AccessGate
        {
            internal readonly SemaphoreSlim Gate = new(1, 1);
            internal int Users;
        }

        private sealed class AccessLease(string path, AccessGate access) : IDisposable
        {
            internal SemaphoreSlim Gate => access.Gate;

            public void Dispose()
            {
                lock (_accessGates)
                {
                    if (--access.Users == 0)
                    {
                        _accessGates.Remove(path);
                        access.Gate.Dispose();
                    }
                }
            }
        }

        internal static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken = default, JsonSerializerOptions options = null)
        {
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            using var access = GetAccess(path);
            await access.Gate.WaitAsync(cancellationToken);
            try
            {
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
            finally
            {
                access.Gate.Release();
            }
        }

        internal static T Read<T>(string path, JsonSerializerOptions options = null)
        {
            using var access = GetAccess(path);
            access.Gate.Wait();
            try
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
            finally
            {
                access.Gate.Release();
            }
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
            using var access = GetAccess(path);
            await access.Gate.WaitAsync(cancellationToken);
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
                    access.Gate.Release();
                }
            }
        }

        // Retained for existing synchronous consumers. Refresh paths use WriteAsync.
        internal static void Write<T>(string path, T value, JsonSerializerOptions options = null)
        {
            using var access = GetAccess(path);
            access.Gate.Wait();
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
                    access.Gate.Release();
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