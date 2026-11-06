using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using StartScreen.Models;

namespace StartScreen.Services
{
    internal sealed class FeedbackCache
    {
        private static readonly byte[] _entropy = Encoding.UTF8.GetBytes("StartScreen.FeedbackCache.v1");
        private readonly string _folder;

        internal FeedbackCache(string folder)
        {
            _folder = folder ?? throw new ArgumentNullException(nameof(folder));
        }

        internal async Task<FeedbackSnapshot> ReadAsync(long principalId, CancellationToken cancellationToken)
        {
            try
            {
                byte[] encrypted = await AtomicJsonCache.ReadAsync<byte[]>(GetPath(principalId), cancellationToken);
                if (encrypted == null)
                    return null;
                byte[] json = ProtectedData.Unprotect(encrypted, _entropy, DataProtectionScope.CurrentUser);
                var snapshot = JsonSerializer.Deserialize<FeedbackSnapshot>(json);
                cancellationToken.ThrowIfCancellationRequested();
                if (snapshot == null || snapshot.PrincipalId != principalId || snapshot.Tickets == null
                    || snapshot.UpdatedUtc == default || snapshot.Tickets.Any(ticket => ticket == null || ticket.Id <= 0))
                    throw new InvalidDataException("The Feedback cache snapshot is invalid or belongs to a different account.");
                return snapshot;
            }
            catch (Exception ex) when (IsCacheFailure(ex))
            {
                await ex.LogAsync();
                OutputLog.Info("Feedback", "Disk cache could not be read. Requesting a fresh snapshot from Developer Community.");
                return null;
            }
        }

        internal async Task WriteAsync(FeedbackSnapshot snapshot, CancellationToken cancellationToken)
        {
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(snapshot);
                byte[] encrypted = ProtectedData.Protect(json, _entropy, DataProtectionScope.CurrentUser);
                await AtomicJsonCache.WriteAsync(GetPath(snapshot.PrincipalId), encrypted, cancellationToken);
                OutputLog.Info("Feedback", $"Saved {snapshot.Tickets.Count} tickets to the encrypted disk cache.");
            }
            catch (Exception ex) when (IsCacheFailure(ex))
            {
                await ex.LogAsync();
                OutputLog.Info("Feedback", "Disk cache could not be saved. The downloaded tickets are still available in this session.");
            }
        }

        private string GetPath(long principalId) => Path.Combine(_folder, principalId.ToString(CultureInfo.InvariantCulture) + ".json");

        private static bool IsCacheFailure(Exception exception) => exception is IOException || exception is InvalidDataException
            || exception is UnauthorizedAccessException || exception is CryptographicException || exception is JsonException;
    }
}