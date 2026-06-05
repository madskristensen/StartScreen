using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StartScreen.Services.DevHub
{
    /// <summary>
    /// Credential acquisition for Dev Hub providers.
    /// Tries Git Credential Manager first, falls back to Windows Credential Manager.
    /// </summary>
    internal static class DevHubCredentialHelper
    {
        private static readonly Dictionary<string, DevHubCredential> _credentialCache = new Dictionary<string, DevHubCredential>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _cacheLock = new object();

        /// <summary>
        /// Attempts to acquire a credential (username + token) for the given host.
        /// Caches the result to avoid repeated GCM prompts.
        /// Tries GCM first, then falls back to a stored credential in Windows Credential Manager.
        /// Returns null if no credential is available.
        /// </summary>
        public static Task<DevHubCredential> GetCredentialAsync(string host, CancellationToken cancellationToken)
        {
            return GetCredentialAsync(host, username: null, cancellationToken);
        }

        /// <summary>
        /// Attempts to acquire a credential (username + token) for the given host and optional username.
        /// When username is provided, this asks GCM for that specific account.
        /// </summary>
        public static async Task<DevHubCredential> GetCredentialAsync(string host, string username, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(host))
                return null;

            var normalizedUsername = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
            var cacheKey = GetCacheKey(host, normalizedUsername);

            // Return cached credential if available
            var cached = TryGetCachedCredential(cacheKey);
            if (cached != null)
                return cached;

            // Try Git Credential Manager
            var gcmResult = await TryGitCredentialManagerAsync(host, normalizedUsername, cancellationToken);
            if (gcmResult != null)
            {
                CacheCredential(cacheKey, gcmResult);
                return gcmResult;
            }

            // Fallback: Windows Credential Manager
            var stored = TryWindowsCredentialManager(host);
            if (stored != null &&
                (normalizedUsername == null ||
                 string.Equals(normalizedUsername, stored.Username, StringComparison.OrdinalIgnoreCase)))
            {
                CacheCredential(cacheKey, stored);
                return stored;
            }

            return null;
        }

        /// <summary>
        /// Clears cached credentials, forcing re-acquisition on next request.
        /// </summary>
        public static void ClearCachedCredentials()
        {
            lock (_cacheLock)
            {
                _credentialCache.Clear();
            }
        }

        /// <summary>
        /// Invalidates the cached credential for a specific host.
        /// Call this when an API returns 401 Unauthorized so the next
        /// request re-acquires a fresh token from GCM.
        /// </summary>
        public static void InvalidateCachedCredential(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return;

            RemoveCachedCredentialsForHost(host);
        }

        /// <summary>
        /// Stores a user-provided credential in Windows Credential Manager.
        /// Used when the user manually enters a PAT.
        /// </summary>
        public static void StoreCredential(string host, string username, string token)
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(token))
                return;

            try
            {
                NativeCredentialManager.Write(
                    $"StartScreen:{host}",
                    username ?? string.Empty,
                    token);
            }
            catch (Exception ex)
            {
                ex.Log();
            }
        }

        /// <summary>
        /// Removes a stored credential from Windows Credential Manager.
        /// </summary>
        public static void RemoveCredential(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return;

            try
            {
                NativeCredentialManager.Delete($"StartScreen:{host}");
            }
            catch (Exception ex)
            {
                ex.Log();
            }
        }

        /// <summary>
        /// Checks if a credential exists for the given host (without retrieving it).
        /// </summary>
        public static bool HasCredential(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return false;

            // Check Windows Credential Manager for our stored cred
            var stored = NativeCredentialManager.Read($"StartScreen:{host}");
            if (stored != null)
                return true;

            return false;
        }

        /// <summary>
        /// Lists GitHub accounts known to Git Credential Manager.
        /// </summary>
        public static async Task<IReadOnlyList<string>> GetGitHubAccountsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "credential-manager github list",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

                using (var process = Process.Start(psi))
                {
                    if (process == null)
                        return Array.Empty<string>();

                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var completed = await Task.WhenAny(
                        outputTask,
                        Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));

                    if (completed != outputTask)
                    {
                        TryKillProcess(process);
                        return Array.Empty<string>();
                    }

                    if (!process.WaitForExit(3000))
                    {
                        TryKillProcess(process);
                        return Array.Empty<string>();
                    }

                    if (process.ExitCode != 0)
                        return Array.Empty<string>();

                    return ParseGitHubAccountListOutput(await outputTask);
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Removes a specific GitHub account from Git Credential Manager.
        /// </summary>
        public static async Task<bool> RemoveGitHubAccountAsync(string account, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(account))
                return false;

            try
            {
                var escapedAccount = account.Trim().Replace("\"", "\\\"");
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"credential-manager github logout \"{escapedAccount}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

                using (var process = Process.Start(psi))
                {
                    if (process == null)
                        return false;

                    var waitTask = Task.Run(() => process.WaitForExit(8000), cancellationToken);
                    var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(8), cancellationToken));
                    if (completed != waitTask || !waitTask.Result)
                    {
                        TryKillProcess(process);
                        return false;
                    }

                    var success = process.ExitCode == 0;
                    if (success)
                    {
                        InvalidateCachedCredential("github.com");
                    }

                    return success;
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                return false;
            }
        }

        /// <summary>
        /// Calls GCM interactively, allowing it to open a browser for OAuth.
        /// Used when the user explicitly clicks "Connect account".
        /// </summary>
        public static async Task<bool> ConnectInteractiveAsync(string host, CancellationToken cancellationToken)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "credential fill",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                // Allow GCM to open browser / show interactive prompts for OAuth.
                using (var process = Process.Start(psi))
                {
                    if (process == null)
                        return false;

                    await process.StandardInput.WriteAsync(BuildGitCredentialInput(host));
                    process.StandardInput.Close();

                    // Wait up to 2 minutes for the user to complete the browser OAuth flow.
                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var completed = await Task.WhenAny(
                        outputTask,
                        Task.Delay(TimeSpan.FromMinutes(2), cancellationToken));

                    if (completed != outputTask)
                    {
                        TryKillProcess(process);
                        return false;
                    }

                    if (!process.WaitForExit(5000))
                    {
                        TryKillProcess(process);
                        return false;
                    }

                    if (process.ExitCode != 0)
                        return false;

                    var credential = ParseGitCredentialOutput(await outputTask);
                    if (credential == null)
                        return false;

                    // Cache the freshly acquired credential.
                    CacheCredential(GetCacheKey(host, username: null), credential);

                    // Also persist to Windows Credential Manager so the credential
                    // survives VS restarts even if GCM can't refresh non-interactively.
                    StoreCredential(host, credential.Username, credential.Token);

                    return true;
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                return false;
            }
        }

        private static async Task<DevHubCredential> TryGitCredentialManagerAsync(string host, string username, CancellationToken cancellationToken)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "credential fill",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                // Prevent GCM from opening interactive prompts (browser, dialog).
                // Only return credentials already cached/stored.
                psi.Environment["GCM_INTERACTIVE"] = "never";
                psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

                using (var process = Process.Start(psi))
                {
                    if (process == null)
                        return null;

                    // Write the protocol/host (+ optional username) to stdin.
                    await process.StandardInput.WriteAsync(BuildGitCredentialInput(host, username));
                    process.StandardInput.Close();

                    // Read output with timeout
                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var completed = await Task.WhenAny(
                        outputTask,
                        Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));

                    if (completed != outputTask)
                    {
                        TryKillProcess(process);
                        return null;
                    }

                    if (!process.WaitForExit(3000))
                    {
                        TryKillProcess(process);
                        return null;
                    }

                    if (process.ExitCode != 0)
                        return null;

                    return ParseGitCredentialOutput(await outputTask);
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                return null;
            }
        }

        internal static DevHubCredential ParseGitCredentialOutput(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return null;

            string username = null;
            string password = null;

            foreach (var line in output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var equalsIndex = line.IndexOf('=');
                if (equalsIndex <= 0)
                    continue;

                var key = line.Substring(0, equalsIndex).Trim();
                var value = line.Substring(equalsIndex + 1).Trim();

                if (key.Equals("username", StringComparison.OrdinalIgnoreCase))
                    username = value;
                else if (key.Equals("password", StringComparison.OrdinalIgnoreCase))
                    password = value;
            }

            if (string.IsNullOrEmpty(password))
                return null;

            return new DevHubCredential(username, password);
        }

        internal static IReadOnlyList<string> ParseGitHubAccountListOutput(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return Array.Empty<string>();

            var accounts = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rawLine in output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line))
                    continue;

                if (line.StartsWith("warning:", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (seen.Add(line))
                {
                    accounts.Add(line);
                }
            }

            return accounts;
        }

        private static string BuildGitCredentialInput(string host, string username = null)
        {
            var builder = new StringBuilder();
            builder.AppendLine("protocol=https");
            builder.AppendLine($"host={host}");

            if (!string.IsNullOrWhiteSpace(username))
            {
                builder.AppendLine($"username={username.Trim()}");
            }

            // Empty line terminates input for `git credential fill`.
            builder.AppendLine();
            return builder.ToString();
        }

        private static string GetCacheKey(string host, string username)
        {
            return $"{host}\n{username ?? string.Empty}";
        }

        private static DevHubCredential TryGetCachedCredential(string key)
        {
            lock (_cacheLock)
            {
                if (_credentialCache.TryGetValue(key, out var cached))
                    return cached;
            }

            return null;
        }

        private static void CacheCredential(string key, DevHubCredential credential)
        {
            lock (_cacheLock)
            {
                _credentialCache[key] = credential;
            }
        }

        private static void RemoveCachedCredentialsForHost(string host)
        {
            lock (_cacheLock)
            {
                var prefix = $"{host}\n";
                var keys = _credentialCache.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                for (var i = 0; i < keys.Count; i++)
                {
                    _credentialCache.Remove(keys[i]);
                }
            }
        }

        private static DevHubCredential TryWindowsCredentialManager(string host)
        {
            try
            {
                var cred = NativeCredentialManager.Read($"StartScreen:{host}");
                if (cred != null)
                {
                    return new DevHubCredential(cred.Value.username, cred.Value.password);
                }
            }
            catch (Exception ex)
            {
                ex.Log();
            }

            return null;
        }

        private static void TryKillProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch (Exception ex)
            {
                ex.Log();
            }
        }
    }

    /// <summary>
    /// Represents an acquired credential (username + token).
    /// </summary>
    internal sealed class DevHubCredential
    {
        public string Username { get; }
        public string Token { get; }

        public DevHubCredential(string username, string token)
        {
            Username = username;
            Token = token ?? throw new ArgumentNullException(nameof(token));
        }
    }
}
