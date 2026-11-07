using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StartScreen.Services.DevHub
{
    internal static class DevHubMetadataCache
    {
        private static readonly ConditionalWeakTable<object, ClientScope> _clients = new ConditionalWeakTable<object, ClientScope>();

        internal static string GetClientScope(object client) => _clients.GetValue(client, _ => new ClientScope()).Id;

        private sealed class ClientScope
        {
            internal string Id { get; } = Guid.NewGuid().ToString();
        }

        private static readonly SharedRefresh<string> _completedRuns = new SharedRefresh<string>(successLifetime: TimeSpan.FromHours(24));

        internal static string GetKey(DevHubCredential credential, string resource)
        {
            using var hash = SHA256.Create();
            var account = Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(
                credential.Username + "|" + credential.Token + "|" + credential.IsBearer)));
            return DevHubCredentialHelper.CredentialGeneration + "|" + account + "|" + resource;
        }

        internal static Task<string> GetCompletedRunAsync(DevHubCredential credential, string resource,
            Func<CancellationToken, Task<string>> fetch, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (credential.ExpiresOn <= DateTimeOffset.UtcNow)
                throw new UnauthorizedAccessException("The credential has expired.");
            return _completedRuns.RunAsync(GetKey(credential, resource), false, fetch, value => value != null, token);
        }
    }
}
