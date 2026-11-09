using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using StartScreen.Models;

namespace StartScreen.Services
{
    internal sealed class GitWorktreeCache
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Task<IReadOnlyList<GitWorktreeInfo>>> _requests = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _concurrency = new(2, 2);
        private readonly Func<string, CancellationToken, Task<IReadOnlyList<GitWorktreeInfo>>> _read;
        private int _generation;

        internal GitWorktreeCache(Func<string, CancellationToken, Task<IReadOnlyList<GitWorktreeInfo>>> read = null)
        {
            _read = read ?? GitWorktreeService.ReadAsync;
        }

        internal int Generation { get { lock (_gate) return _generation; } }

        internal void Invalidate()
        {
            lock (_gate)
            {
                _generation++;
                _requests.Clear();
            }
        }

        internal async Task<IReadOnlyList<GitWorktreeInfo>> GetAsync(string workspacePath, CancellationToken token)
        {
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            token.ThrowIfCancellationRequested();
            var directory = Directory.Exists(workspacePath) ? workspacePath
                : File.Exists(workspacePath) ? Path.GetDirectoryName(workspacePath)
                : throw new DirectoryNotFoundException("The workspace path could not be found: " + workspacePath);
            var gitDirectory = LibGit2Sharp.Repository.Discover(directory);
            if (gitDirectory == null)
                return Array.Empty<GitWorktreeInfo>();
            var commonFile = Path.Combine(gitDirectory, "commondir");
            var key = Path.GetFullPath(File.Exists(commonFile)
                ? Path.Combine(gitDirectory, File.ReadAllText(commonFile).Trim()) : gitDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Task<IReadOnlyList<GitWorktreeInfo>> request;
            lock (_gate)
            {
                if (!_requests.TryGetValue(key, out request) || request.IsCanceled)
                {
                    request = ReadAsync(directory, token);
                    _requests[key] = request;
                }
            }
            return GitWorktreeService.ForWorkspace(await request.WithCancellation(token), directory);
        }

        private async Task<IReadOnlyList<GitWorktreeInfo>> ReadAsync(string directory, CancellationToken token)
        {
            await _concurrency.WaitAsync(token);
            try
            {
                return await _read(directory, token);
            }
            finally
            {
                _concurrency.Release();
            }
        }
    }
}
