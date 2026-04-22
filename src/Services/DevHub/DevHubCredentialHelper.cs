using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        public static async Task<DevHubCredential> GetCredentialAsync(string host, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(host))
                return null;

            // Return cached credential if available
            lock (_cacheLock)
            {
                if (_credentialCache.TryGetValue(host, out var cached))
                    return cached;
            }

            // Try Git Credential Manager
            var gcmResult = await TryGitCredentialManagerAsync(host, cancellationToken);
            if (gcmResult != null)
            {
                lock (_cacheLock)
                {
                    _credentialCache[host] = gcmResult;
                }
                return gcmResult;
            }

            // Fallback: Windows Credential Manager
            var stored = TryWindowsCredentialManager(host);
            if (stored != null)
            {
                lock (_cacheLock)
                {
                    _credentialCache[host] = stored;
                }
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

                    await process.StandardInput.WriteLineAsync("protocol=https");
                    await process.StandardInput.WriteLineAsync($"host={host}");
                    await process.StandardInput.WriteLineAsync();
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
                    lock (_cacheLock)
                    {
                        _credentialCache[host] = credential;
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                return false;
            }
        }

        private static async Task<DevHubCredential> TryGitCredentialManagerAsync(string host, CancellationToken cancellationToken)
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

                    // Write the protocol/host to stdin
                    await process.StandardInput.WriteLineAsync($"protocol=https");
                    await process.StandardInput.WriteLineAsync($"host={host}");
                    await process.StandardInput.WriteLineAsync();
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
