using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StartScreen.Models.DevHub;

namespace StartScreen.Services.DevHub
{
    /// <summary>Proves that a saved identity was verified with the current credential on this Windows account.</summary>
    internal sealed class DevHubAccountBinding
    {
        private const string _keyTarget = "StartScreen:ActivityAccountBinding";
        internal static DevHubAccountBinding Default { get; } = new();
        private readonly object _gate = new();
        private readonly Func<string> _readKey;
        private readonly Action<string> _writeKey;
        private readonly Func<DateTimeOffset> _utcNow;
        private bool _disabled;

        internal DevHubAccountBinding(Func<string> readKey = null, Action<string> writeKey = null,
            Func<DateTimeOffset> utcNow = null)
        {
            _readKey = readKey ?? (() => NativeCredentialManager.Read(_keyTarget)?.password);
            _writeKey = writeKey ?? (key => NativeCredentialManager.Write(_keyTarget, string.Empty, key));
            _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        }

        internal void Remember(DevHubCredential credential, string resource, DevHubUser user)
        {
            lock (_gate)
            {
                try
                {
                    if (_disabled || credential.ExpiresOn <= _utcNow())
                        return;
                    var key = _readKey();
                    if (string.IsNullOrEmpty(key))
                    {
                        key = NewKey();
                        _writeKey(key);
                    }
                    user.AccountBinding = Compute(key, credential, resource, user);
                    user.AccountBindingResource = resource;
                }
                catch (Exception ex)
                {
                    _disabled = true;
                    ex.LogAsync().FireAndForget();
                }
            }
        }

        internal bool Matches(DevHubCredential credential, string resource, DevHubUser user)
        {
            lock (_gate)
            {
                try
                {
                    if (_disabled || credential == null || credential.ExpiresOn <= _utcNow()
                        || string.IsNullOrEmpty(user.AccountBinding) || user.AccountBindingResource != resource)
                        return false;
                    var key = _readKey();
                    if (string.IsNullOrEmpty(key))
                        return false;
                    var expected = Compute(key, credential, resource, user);
                    if (expected.Length != user.AccountBinding.Length)
                        return false;
                    var difference = 0;
                    for (var index = 0; index < expected.Length; index++)
                        difference |= expected[index] ^ user.AccountBinding[index];
                    return difference == 0;
                }
                catch (Exception ex)
                {
                    _disabled = true;
                    ex.LogAsync().FireAndForget();
                    return false;
                }
            }
        }

        internal void Invalidate()
        {
            lock (_gate)
            {
                try
                {
                    _writeKey(NewKey());
                }
                catch (Exception ex)
                {
                    _disabled = true;
                    ex.LogAsync().FireAndForget();
                }
            }
        }

        private static string NewKey()
        {
            var bytes = new byte[32];
            using var random = RandomNumberGenerator.Create();
            random.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }

        private static string Compute(string key, DevHubCredential credential, string resource, DevHubUser user)
        {
            var payload = JsonSerializer.Serialize(new[] { resource, credential.Username, credential.Token,
                credential.IsBearer.ToString(), user.ProviderName, user.Host, user.IdentityId, user.Username });
            using var hmac = new HMACSHA256(Convert.FromBase64String(key));
            return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
        }
    }
}
