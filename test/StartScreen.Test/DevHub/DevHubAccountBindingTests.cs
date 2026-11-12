using System.Text.Json;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubAccountBindingTests
    {
        [TestMethod]
        [DataRow("token")]
        [DataRow("username")]
        [DataRow("bearer")]
        [DataRow("resource")]
        [DataRow("identity")]
        [DataRow("host")]
        [DataRow("provider")]
        [DataRow("signature")]
        [DataRow("key")]
        public void BindingRejectsChangedCredentialsAccountsEndpointsAndSignatures(string change)
        {
            var key = Key(1);
            var binding = new DevHubAccountBinding(() => key, value => key = value);
            var credential = new DevHubCredential("me", "test-token");
            var user = User();
            var resource = "https://example.com/user";
            binding.Remember(credential, resource, user);
            Assert.IsTrue(binding.Matches(credential, resource, user));
            switch (change)
            {
                case "token": credential = new DevHubCredential("me", "other-token"); break;
                case "username": credential = new DevHubCredential("other", "test-token"); break;
                case "bearer": credential = new DevHubCredential("test-token", DateTimeOffset.MaxValue); break;
                case "resource": resource += "/other"; break;
                case "identity": user.IdentityId = "other"; break;
                case "host": user.Host = "other.example.com"; break;
                case "provider": user.ProviderName = "Other"; break;
                case "signature": user.AccountBinding = user.AccountBinding.Substring(1); break;
                case "key": key = Key(2); break;
            }
            Assert.IsFalse(binding.Matches(credential, resource, user));
        }

        [TestMethod]
        public void BindingSurvivesSerializationAndRestartWithoutPersistingSecrets()
        {
            var key = Key(1);
            var credential = new DevHubCredential("me", "test-token");
            var user = User();
            var binding = new DevHubAccountBinding(() => key, value => key = value);
            binding.Remember(credential, "profile", user);
            var json = JsonSerializer.Serialize(user);
            var reopened = JsonSerializer.Deserialize<DevHubUser>(json)!;
            var restarted = new DevHubAccountBinding(() => key, value => key = value);

            Assert.IsTrue(restarted.Matches(credential, "profile", reopened));
            Assert.DoesNotContain("test-token", json);
            Assert.DoesNotContain(key, json);
            restarted.Invalidate();
            Assert.IsFalse(binding.Matches(credential, "profile", reopened));
        }

        [TestMethod]
        public void BindingCannotOutliveBearerCredentialOrRestoreLegacyProfiles()
        {
            var now = DateTimeOffset.UtcNow;
            var key = Key(1);
            var binding = new DevHubAccountBinding(() => key, value => key = value, () => now);
            var credential = new DevHubCredential("test-token", now.AddMinutes(1));
            var user = User();
            Assert.IsFalse(binding.Matches(credential, "profile", user));
            binding.Remember(credential, "profile", user);
            Assert.IsTrue(binding.Matches(credential, "profile", user));
            now = now.AddMinutes(1);
            Assert.IsFalse(binding.Matches(credential, "profile", user));
        }

        [TestMethod]
        public void BindingFailureDisablesRestorationRatherThanTrustingAnOldKey()
        {
            var key = Key(1);
            var binding = new DevHubAccountBinding(() => key, _ => throw new System.IO.IOException("Key storage unavailable"));
            var credential = new DevHubCredential("me", "test-token");
            var user = User();
            binding.Remember(credential, "profile", user);
            Assert.IsTrue(binding.Matches(credential, "profile", user));
            binding.Invalidate();
            Assert.IsFalse(binding.Matches(credential, "profile", user));
        }

        private static string Key(byte value) => Convert.ToBase64String(Enumerable.Repeat(value, 32).ToArray());
        private static DevHubUser User() => new() { Username = "me", IdentityId = "1", Host = "example.com", ProviderName = "Example" };
    }
}
