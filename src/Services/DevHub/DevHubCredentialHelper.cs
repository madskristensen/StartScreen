using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StartScreen.Helpers;

namespace StartScreen.Services.DevHub
{
    /// <summary>
    /// Credential acquisition for Dev Hub providers.
    /// Tries Git Credential Manager first, falls back to Windows Credential Manager.
    /// </summary>
    internal static class DevHubCredentialHelper
    {
        private static readonly Dictionary<string, CachedCredential> _credentialCache = new Dictionary<string, CachedCredential>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _cacheLock = new object();
        private static readonly Dictionary<string, int> _hostGenerations = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static int _credentialGeneration;
        private static int _allCredentialsGeneration;
        private static int _accountGeneration;
        internal static int CredentialGeneration => Volatile.Read(ref _credentialGeneration);
        // Token retries must not invalidate dashboards for otherwise unchanged accounts.
        internal static int AccountGeneration => Volatile.Read(ref _accountGeneration);
        private static readonly Lazy<AzureDevOpsCredentialProvider> _azureDevOpsCredentials = new Lazy<AzureDevOpsCredentialProvider>(() =>
            new AzureDevOpsCredentialProvider(new Azure.Identity.VisualStudioCredential(new Azure.Identity.VisualStudioCredentialOptions
            {
                ProcessTimeout = TimeSpan.FromSeconds(15),
            }), new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) }));

        internal static async Task<DevHubCredential> GetAzureDevOpsCredentialAsync(string host, string requestUrl, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!host.Equals(AzureDevOpsServerHelper.CloudHost, StringComparison.OrdinalIgnoreCase))
                return await GetCredentialAsync(host, cancellationToken);

            int generation;
            AzureDevOpsAuthenticationMode mode;
            lock (_cacheLock)
            {
                generation = GetHostGeneration(host);
                mode = Options.Instance.DevHubAdoAuthentication;
            }
            if (mode == AzureDevOpsAuthenticationMode.Disconnected)
                return null;

            var stored = mode == AzureDevOpsAuthenticationMode.VisualStudio ? null : TryWindowsCredentialManager(host);
            var credential = !UsesVisualStudio(mode, stored != null)
                ? stored ?? await GetCredentialAsync(host, cancellationToken)
                : await _azureDevOpsCredentials.Value.GetCredentialAsync(requestUrl, cancellationToken);
            lock (_cacheLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return generation == GetHostGeneration(host) && mode == Options.Instance.DevHubAdoAuthentication
                    ? credential : null;
            }
        }

        internal static bool UsesVisualStudio(AzureDevOpsAuthenticationMode mode, bool hasStoredPat) =>
            mode == AzureDevOpsAuthenticationMode.VisualStudio
            || (mode == AzureDevOpsAuthenticationMode.Automatic && !hasStoredPat);

        internal static void InvalidateAzureDevOpsCredential(DevHubCredential credential)
        {
            lock (_cacheLock)
            {
                DevHubAccountBinding.Default.Invalidate();
                _hostGenerations[AzureDevOpsServerHelper.CloudHost] = ++_credentialGeneration;
                if (credential.IsBearer)
                {
                    if (_azureDevOpsCredentials.IsValueCreated)
                        _azureDevOpsCredentials.Value.Invalidate(credential);
                }
                else
                {
                    RemoveCachedCredentialsForHost(AzureDevOpsServerHelper.CloudHost);
                }
            }
        }

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
        public static Task<DevHubCredential> GetCredentialAsync(string host, string username, CancellationToken cancellationToken)
        {
            return GetCredentialAsync(host, username, cancellationToken, TryGitCredentialManagerAsync,
                TryWindowsCredentialManager, GetSelectedGitHubAccount);
        }

        internal static async Task<DevHubCredential> GetCredentialAsync(string host, string username, CancellationToken cancellationToken,
            Func<string, string, CancellationToken, Task<DevHubCredential>> acquire,
            Func<string, DevHubCredential> readStored, Func<string> selectedAccount)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(host))
                return null;

            var normalizedUsername = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
            var cacheKey = GetCacheKey(host, normalizedUsername);
            int generation;
            string account;
            lock (_cacheLock)
            {
                generation = GetHostGeneration(host);
                account = selectedAccount();
                cancellationToken.ThrowIfCancellationRequested();
                if (_credentialCache.TryGetValue(cacheKey, out var cached) &&
                    (normalizedUsername != null || string.Equals(cached.Account, account, StringComparison.OrdinalIgnoreCase)) &&
                    IsAcquisitionCurrent(generation, host, account, selectedAccount))
                    return cached.Credential;
            }

            var credential = await acquire(host, normalizedUsername, cancellationToken);
            lock (_cacheLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsAcquisitionCurrent(generation, host, account, selectedAccount))
                    return null;
            }

            credential ??= readStored(host);
            lock (_cacheLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsAcquisitionCurrent(generation, host, account, selectedAccount) || credential == null ||
                    (normalizedUsername != null && !string.Equals(normalizedUsername, credential.Username, StringComparison.OrdinalIgnoreCase)))
                    return null;

                _credentialCache[cacheKey] = new CachedCredential(credential, account);
                return credential;
            }
        }

        /// <summary>
        /// Clears cached credentials, forcing re-acquisition on next request.
        /// </summary>
        public static void ClearCachedCredentials()
        {
            lock (_cacheLock)
            {
                DevHubAccountBinding.Default.Invalidate();
                _allCredentialsGeneration = ++_credentialGeneration;
                _accountGeneration++;
                _hostGenerations.Clear();
                _credentialCache.Clear();
                if (_azureDevOpsCredentials.IsValueCreated)
                    _azureDevOpsCredentials.Value.Invalidate();
            }
        }

        /// <summary>Invalidates verified identities after an activity API rejects authentication.</summary>
        internal static void InvalidateAccountVerification()
        {
            lock (_cacheLock)
            {
                DevHubAccountBinding.Default.Invalidate();
                _credentialGeneration++;
            }
        }

        /// <summary>Invalidates a host credential so the next request reacquires it.</summary>
        public static void InvalidateCachedCredential(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return;

            lock (_cacheLock)
            {
                DevHubAccountBinding.Default.Invalidate();
                _hostGenerations[host] = ++_credentialGeneration;
                RemoveCachedCredentialsForHost(host);
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
                ex.LogAsync().FireAndForget();
            }
        }

        /// <summary>
        /// Removes a stored credential from Windows Credential Manager.
        /// </summary>
        public static void RemoveCredential(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return;

            DevHubAccountBinding.Default.Invalidate();
            try
            {
                NativeCredentialManager.Delete($"StartScreen:{host}");
            }
            catch (Exception ex)
            {
                ex.LogAsync().FireAndForget();
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
                GitHelper.SuppressCredentialPrompts(psi);
                var result = await OwnedProcess.RunAsync(psi, TimeSpan.FromSeconds(5), cancellationToken);
                if (result.TimedOut || result.ExitCode != 0 || result.OutputTruncated)
                {
                    await LogProcessFailureAsync("github list", result);
                    return Array.Empty<string>();
                }

                return ParseGitHubAccountListOutput(result.Output);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
                throw;
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

                GitHelper.SuppressCredentialPrompts(psi);
                var result = await OwnedProcess.RunAsync(psi, TimeSpan.FromSeconds(8), cancellationToken);
                var success = !result.TimedOut && result.ExitCode == 0;
                if (!success || result.OutputTruncated || result.ErrorTruncated)
                    await LogProcessFailureAsync("github logout", result);
                if (success)
                {
                    InvalidateCachedCredential("github.com");
                }

                return success;
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
                throw;
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
        public static Task<bool> ConnectInteractiveAsync(string host, CancellationToken cancellationToken)
        {
            return ConnectInteractiveAsync(host, cancellationToken,
                (target, token) => ReadGitCredentialAsync(target, null, true, token),
                StoreCredential, GetSelectedGitHubAccount);
        }

        internal static async Task<bool> ConnectInteractiveAsync(string host, CancellationToken cancellationToken,
            Func<string, CancellationToken, Task<DevHubCredential>> acquire,
            Action<string, string, string> store, Func<string> selectedAccount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(host))
                return false;

            int generation;
            string account;
            lock (_cacheLock)
            {
                generation = GetHostGeneration(host);
                account = selectedAccount();
            }

            try
            {
                var credential = await acquire(host, cancellationToken);
                lock (_cacheLock)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (credential == null || !IsAcquisitionCurrent(generation, host, account, selectedAccount))
                        return false;

                    // Persistence and publication are serialized with invalidation.
                    // A stale interactive login must not repopulate the default key.
                    store(host, credential.Username, credential.Token);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsAcquisitionCurrent(generation, host, account, selectedAccount))
                        return false;
                    ClearCachedCredentials();
                    _credentialCache[GetCacheKey(host, username: null)] = new CachedCredential(credential, account);
                    return true;
                }
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
                throw;
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
                return await ReadGitCredentialAsync(host, username, false, cancellationToken);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
                throw;
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                return null;
            }
        }

        private static async Task<DevHubCredential> ReadGitCredentialAsync(string host, string username, bool interactive, CancellationToken cancellationToken)
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
            if (!interactive)
                GitHelper.SuppressCredentialPrompts(psi);

            var result = await OwnedProcess.RunAsync(psi,
                interactive ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(5),
                cancellationToken, BuildGitCredentialInput(host, username));
            // Never parse a partial token or log credential-bearing subprocess output.
            if (result.TimedOut || result.ExitCode != 0 || result.OutputTruncated)
            {
                await LogProcessFailureAsync(interactive ? "interactive credential fill" : "credential fill", result);
                return null;
            }

            return ParseGitCredentialOutput(result.Output);
        }

        private static Task LogProcessFailureAsync(string operation, OwnedProcessResult result)
        {
            var ex = new InvalidOperationException(
                $"Git Credential Manager {operation} did not complete cleanly " +
                $"(deadlineExceeded={result.TimedOut}, exitCode={result.ExitCode}, " +
                $"stdoutTruncated={result.OutputTruncated}, stderrTruncated={result.ErrorTruncated}).");
            return ex.LogAsync();
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

        private static string GetSelectedGitHubAccount()
        {
            return Options.Instance.DevHubGitHubAccount?.Trim() ?? string.Empty;
        }

        private static int GetHostGeneration(string host)
        {
            return _hostGenerations.TryGetValue(host, out var generation) ? generation : _allCredentialsGeneration;
        }

        private static bool IsAcquisitionCurrent(int generation, string host, string account, Func<string> selectedAccount)
        {
            return generation == GetHostGeneration(host) &&
                string.Equals(account, selectedAccount(), StringComparison.OrdinalIgnoreCase);
        }

        private sealed class CachedCredential
        {
            public CachedCredential(DevHubCredential credential, string account)
            {
                Credential = credential;
                Account = account;
            }

            public DevHubCredential Credential { get; }
            public string Account { get; }
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
                ex.LogAsync().FireAndForget();
            }

            return null;
        }
    }

    /// <summary>
    /// Represents an acquired credential (username + token).
    /// </summary>
    internal sealed class DevHubCredential
    {
        public string Username { get; }
        public string Token { get; }
        public bool IsBearer { get; }
        public DateTimeOffset ExpiresOn { get; } = DateTimeOffset.MaxValue;

        public DevHubCredential(string username, string token)
        {
            Username = username;
            Token = token ?? throw new ArgumentNullException(nameof(token));
        }

        public DevHubCredential(string token, DateTimeOffset expiresOn) : this(null, token)
        {
            IsBearer = true;
            ExpiresOn = expiresOn;
        }
    }
}
