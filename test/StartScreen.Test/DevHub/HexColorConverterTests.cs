using System.Globalization;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.ToolWindows.Controls;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class HexColorConverterTests
    {
        [TestMethod]
        public void HexColorToBrush_ReturnsCachedInstance()
        {
            var converter = new HexColorToBrushConverter();
            var first = converter.Convert("d73a4a", typeof(Brush), null, CultureInfo.InvariantCulture) as SolidColorBrush;
            var second = converter.Convert("d73a4a", typeof(Brush), null, CultureInfo.InvariantCulture) as SolidColorBrush;

            Assert.IsNotNull(first);
            Assert.AreSame(first, second, "Same hex string should return the same cached brush instance");
        }

        [TestMethod]
        public void HexColorToBrush_ParsesColorCorrectly()
        {
            var converter = new HexColorToBrushConverter();
            var brush = converter.Convert("ff0000", typeof(Brush), null, CultureInfo.InvariantCulture) as SolidColorBrush;

            Assert.IsNotNull(brush);
            Assert.AreEqual(255, brush.Color.R);
            Assert.AreEqual(0, brush.Color.G);
            Assert.AreEqual(0, brush.Color.B);
        }

        [TestMethod]
        public void HexColorToBrush_HandlesHashPrefix()
        {
            var converter = new HexColorToBrushConverter();
            var withHash = converter.Convert("#00ff00", typeof(Brush), null, CultureInfo.InvariantCulture) as SolidColorBrush;
            var withoutHash = converter.Convert("00ff00", typeof(Brush), null, CultureInfo.InvariantCulture) as SolidColorBrush;

            Assert.IsNotNull(withHash);
            Assert.IsNotNull(withoutHash);
            Assert.AreEqual(withHash.Color, withoutHash.Color);
        }

        [TestMethod]
        public void HexColorToBrush_ReturnsFallbackForInvalidInput()
        {
            var converter = new HexColorToBrushConverter();
            var result = converter.Convert("xyz", typeof(Brush), null, CultureInfo.InvariantCulture) as SolidColorBrush;

            Assert.IsNotNull(result);
            Assert.AreEqual(128, result.Color.R);
        }

        [TestMethod]
        public void HexColorToBrush_ReturnsFrozenBrush()
        {
            var converter = new HexColorToBrushConverter();
            var brush = converter.Convert("0000ff", typeof(Brush), null, CultureInfo.InvariantCulture) as SolidColorBrush;

            Assert.IsNotNull(brush);
            Assert.IsTrue(brush.IsFrozen, "Cached brushes should be frozen for thread safety");
        }

        [TestMethod]
        public void HexColorToForeground_DarkColorReturnsLightForeground()
        {
            var converter = new HexColorToForegroundConverter();
            var brush = converter.Convert("000000", typeof(Brush), null, CultureInfo.InvariantCulture) as SolidColorBrush;

            Assert.IsNotNull(brush);
            Assert.AreEqual(255, brush.Color.R, "Black background should get white foreground");
        }

        [TestMethod]
        [DataRow("#12345")]
        [DataRow("######")]
        [DataRow("#1234567")]
        [DataRow("12345600")]
        [DataRow("gggggg")]
        [DataRow("12 456")]
        [DataRow("")]
        [DataRow(null)]
        public void MalformedColors_UseStableFrozenFallbacksWithoutThrowing(string? value)
        {
            var backgrounds = new HexColorToBrushConverter();
            var foregrounds = new HexColorToForegroundConverter();
            var background = (SolidColorBrush)backgrounds.Convert(value, typeof(Brush), null, CultureInfo.InvariantCulture);

            Assert.AreEqual(Color.FromRgb(128, 128, 128), background.Color);
            Assert.IsTrue(background.IsFrozen);
            Assert.AreSame(background, backgrounds.Convert(null, typeof(Brush), null, CultureInfo.InvariantCulture));
            Assert.AreSame(System.Windows.SystemColors.ControlTextBrush,
                foregrounds.Convert(value, typeof(Brush), null, CultureInfo.InvariantCulture));
        }

        [TestMethod]
        public void NormalizedColors_ReuseBrushAndForegroundAcrossPrefixCaseAndWhitespace()
        {
            var backgrounds = new HexColorToBrushConverter();
            var foregrounds = new HexColorToForegroundConverter();
            var background = backgrounds.Convert("aabbcc", typeof(Brush), null, CultureInfo.InvariantCulture);
            var foreground = foregrounds.Convert("aabbcc", typeof(Brush), null, CultureInfo.InvariantCulture);

            Assert.AreSame(background, backgrounds.Convert(" #AaBbCc ", typeof(Brush), null, CultureInfo.InvariantCulture));
            Assert.AreSame(foreground, foregrounds.Convert(" #AaBbCc ", typeof(Brush), null, CultureInfo.InvariantCulture));
        }

        [TestMethod]
        public void BackgroundCache_IsBoundedAndEvictionDoesNotChangeRetainedBrushes()
        {
            var converter = new HexColorToBrushConverter();
            var retained = (SolidColorBrush)converter.Convert("abcdef", typeof(Brush), null, CultureInfo.InvariantCulture);
            for (var index = 0; index < 1024; index++)
                converter.Convert(index.ToString("x6"), typeof(Brush), null, CultureInfo.InvariantCulture);

            var cache = (System.Collections.IDictionary)typeof(HexColorToBrushConverter)
                .GetField("_cache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
            Assert.IsLessThanOrEqualTo(256, cache.Count);
            Assert.AreEqual(Color.FromRgb(171, 205, 239), retained.Color);
            Assert.IsTrue(retained.IsFrozen);
        }

        [TestMethod]
        public void HexColorToForeground_LightColorReturnsDarkForeground()
        {
            var converter = new HexColorToForegroundConverter();
            var brush = converter.Convert("ffffff", typeof(Brush), null, CultureInfo.InvariantCulture) as SolidColorBrush;

            Assert.IsNotNull(brush);
            Assert.AreEqual(30, brush.Color.R, "White background should get dark foreground");
        }

        [TestMethod]
        public void HexColorToForeground_ReturnsSameInstanceForSameLuminanceGroup()
        {
            var converter = new HexColorToForegroundConverter();
            var first = converter.Convert("000000", typeof(Brush), null, CultureInfo.InvariantCulture);
            var second = converter.Convert("111111", typeof(Brush), null, CultureInfo.InvariantCulture);

            Assert.AreSame(first, second, "Both dark colors should return the same cached light brush");
        }
    }
}
