using StartScreen.Services;

namespace StartScreen.Test
{
    [TestClass]
    public class TipProviderTests
    {
        [TestMethod]
        public void GetTipAt_WhenNegativeIndex_WrapsAround()
        {
            var tip = TipProvider.GetTipAt(-1);

            Assert.IsFalse(string.IsNullOrWhiteSpace(tip));
            Assert.AreEqual(TipProvider.GetTipAt(TipProvider.TipCount - 1), tip);
        }

        [TestMethod]
        public void GetTipAt_WhenBeyondCount_WrapsAround()
        {
            var tip = TipProvider.GetTipAt(TipProvider.TipCount);

            Assert.AreEqual(TipProvider.GetTipAt(0), tip);
        }

        [TestMethod]
        public void GetTipAt_WhenLargeNegative_WrapsCorrectly()
        {
            var tip = TipProvider.GetTipAt(-TipProvider.TipCount * 3 - 1);

            Assert.AreEqual(TipProvider.GetTipAt(TipProvider.TipCount - 1), tip);
        }

        [TestMethod]
        public void GetTipOfTheDay_ReturnsNonEmpty()
        {
            var tip = TipProvider.GetTipOfTheDay();

            Assert.IsFalse(string.IsNullOrWhiteSpace(tip));
        }
    }
}
