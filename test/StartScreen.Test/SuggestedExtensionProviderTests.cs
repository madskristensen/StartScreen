using StartScreen.Services;

namespace StartScreen.Test
{
    [TestClass]
    public class SuggestedExtensionProviderTests
    {
        [TestMethod]
        public void SuggestionCount_IsGreaterThanZero()
        {
            Assert.IsTrue(SuggestedExtensionProvider.SuggestionCount > 0, "SuggestionCount should be greater than 0");
        }

        [TestMethod]
        public void GetSuggestionAt_WhenNegativeIndex_WrapsAround()
        {
            var suggestion = SuggestedExtensionProvider.GetSuggestionAt(-1);

            Assert.IsNotNull(suggestion);
            var expected = SuggestedExtensionProvider.GetSuggestionAt(SuggestedExtensionProvider.SuggestionCount - 1);
            Assert.AreEqual(expected.Id, suggestion.Id);
        }

        [TestMethod]
        public void GetSuggestionAt_WhenBeyondCount_WrapsAround()
        {
            var suggestion = SuggestedExtensionProvider.GetSuggestionAt(SuggestedExtensionProvider.SuggestionCount);

            Assert.IsNotNull(suggestion);
            var expected = SuggestedExtensionProvider.GetSuggestionAt(0);
            Assert.AreEqual(expected.Id, suggestion.Id);
        }

        [TestMethod]
        public void GetSuggestionAt_WhenLargeNegative_WrapsCorrectly()
        {
            var suggestion = SuggestedExtensionProvider.GetSuggestionAt(-SuggestedExtensionProvider.SuggestionCount * 3 - 1);

            Assert.IsNotNull(suggestion);
            var expected = SuggestedExtensionProvider.GetSuggestionAt(SuggestedExtensionProvider.SuggestionCount - 1);
            Assert.AreEqual(expected.Id, suggestion.Id);
        }

        [TestMethod]
        public void GetSuggestionOfTheDay_ReturnsNonNull()
        {
            var suggestion = SuggestedExtensionProvider.GetSuggestionOfTheDay();

            Assert.IsNotNull(suggestion);
            Assert.IsFalse(string.IsNullOrWhiteSpace(suggestion.Id));
            Assert.IsFalse(string.IsNullOrWhiteSpace(suggestion.Name));
        }

        [TestMethod]
        public void Extensions_ReturnsAllExtensions()
        {
            var extensions = SuggestedExtensionProvider.Extensions;

            Assert.IsNotNull(extensions);
            Assert.IsTrue(extensions.Count == SuggestedExtensionProvider.SuggestionCount, "Extensions count should match SuggestionCount");
        }

        [TestMethod]
        public void LoadedExtensions_HaveRequiredProperties()
        {
            var extensions = SuggestedExtensionProvider.Extensions;

            foreach (var extension in extensions)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(extension.Id), "Extension ID should not be null or empty");
                Assert.IsFalse(string.IsNullOrWhiteSpace(extension.Name), "Extension Name should not be null or empty");
                Assert.IsFalse(string.IsNullOrWhiteSpace(extension.Description), "Extension Description should not be null or empty");
                Assert.IsFalse(string.IsNullOrWhiteSpace(extension.MarketplaceUrl), "Extension MarketplaceUrl should not be null or empty");
            }
        }

        [TestMethod]
        public void GetSuggestionAt_WhenValidIndex_ReturnsDifferentExtensions()
        {
            if (SuggestedExtensionProvider.SuggestionCount < 2)
            {
                Assert.Inconclusive("Test requires at least 2 extensions");
                return;
            }

            var first = SuggestedExtensionProvider.GetSuggestionAt(0);
            var second = SuggestedExtensionProvider.GetSuggestionAt(1);

            Assert.AreNotEqual(first.Id, second.Id);
        }
    }
}
