using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace StartScreen.ToolWindows
{
    public enum TypographyRole
    {
        Inherit,
        Body,
        Metadata,
        Heading,
        SectionHeading,
    }

    public static class Typography
    {
        public static readonly DependencyProperty EnvironmentSizeProperty = DependencyProperty.RegisterAttached(
            "EnvironmentSize", typeof(double), typeof(Typography), new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.Inherits));

        public static readonly DependencyProperty RoleProperty = DependencyProperty.RegisterAttached(
            "Role", typeof(TypographyRole), typeof(Typography), new PropertyMetadata(TypographyRole.Inherit, OnRoleChanged));

        public static readonly DependencyProperty LineCountProperty = DependencyProperty.RegisterAttached(
            "LineCount", typeof(int), typeof(Typography), new PropertyMetadata(0, OnLineCountChanged), value => (int)value >= 0);
        public static readonly DependencyProperty MaxLineCountProperty = DependencyProperty.RegisterAttached(
            "MaxLineCount", typeof(int), typeof(Typography), new PropertyMetadata(0, OnMaxLineCountChanged), value => (int)value >= 0);

        private static readonly IValueConverter _fontSizeConverter = new FontSizeConverter();
        private static readonly IMultiValueConverter _lineHeightConverter = new LineHeightConverter();

        public static double GetEnvironmentSize(DependencyObject element) => (double)element.GetValue(EnvironmentSizeProperty);
        public static void SetEnvironmentSize(DependencyObject element, double value) => element.SetValue(EnvironmentSizeProperty, value);
        public static TypographyRole GetRole(DependencyObject element) => (TypographyRole)element.GetValue(RoleProperty);
        public static void SetRole(DependencyObject element, TypographyRole value) => element.SetValue(RoleProperty, value);
        public static int GetLineCount(DependencyObject element) => (int)element.GetValue(LineCountProperty);
        public static void SetLineCount(DependencyObject element, int value) => element.SetValue(LineCountProperty, value);
        public static int GetMaxLineCount(DependencyObject element) => (int)element.GetValue(MaxLineCountProperty);
        public static void SetMaxLineCount(DependencyObject element, int value) => element.SetValue(MaxLineCountProperty, value);

        private static void OnLineCountChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
        {
            ApplyLineCount((TextBlock)element, (int)args.NewValue, FrameworkElement.HeightProperty);
        }

        private static void OnMaxLineCountChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
        {
            ApplyLineCount((TextBlock)element, (int)args.NewValue, FrameworkElement.MaxHeightProperty);
        }

        private static void ApplyLineCount(TextBlock text, int lines, DependencyProperty heightProperty)
        {
            if (lines == 0)
            {
                BindingOperations.ClearBinding(text, heightProperty);
                BindingOperations.ClearBinding(text, TextBlock.LineHeightProperty);
                text.ClearValue(TextBlock.LineStackingStrategyProperty);
                return;
            }

            text.SetCurrentValue(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight);
            BindingOperations.SetBinding(text, TextBlock.LineHeightProperty, CreateLineHeightBinding(text, 1));
            BindingOperations.SetBinding(text, heightProperty, CreateLineHeightBinding(text, lines));
        }

        private static MultiBinding CreateLineHeightBinding(TextBlock text, int lines)
        {
            var binding = new MultiBinding { Converter = _lineHeightConverter, ConverterParameter = lines };
            binding.Bindings.Add(new Binding(nameof(TextBlock.FontSize)) { Source = text });
            binding.Bindings.Add(new Binding(nameof(TextBlock.FontFamily)) { Source = text });
            return binding;
        }

        private sealed class LineHeightConverter : IMultiValueConverter
        {
            public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
                => (double)values[0] * ((FontFamily)values[1]).LineSpacing * (int)parameter;

            public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
                => throw new NotSupportedException();
        }

        private static void OnRoleChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
        {
            if ((TypographyRole)args.NewValue == TypographyRole.Inherit)
            {
                BindingOperations.ClearBinding(element, TextElement.FontSizeProperty);
                return;
            }

            // Bind to the inherited environment size, not FontSize, so nested roles never compound.
            BindingOperations.SetBinding(element, TextElement.FontSizeProperty, new Binding
            {
                Source = element,
                Path = new PropertyPath(EnvironmentSizeProperty),
                Converter = _fontSizeConverter,
                ConverterParameter = args.NewValue,
                Mode = BindingMode.OneWay,
            });
        }

        private sealed class FontSizeConverter : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                var size = (double)value;
                return (TypographyRole)parameter switch
                {
                    TypographyRole.Metadata => size * 11 / 12,
                    TypographyRole.Heading => size * 20 / 12,
                    TypographyRole.SectionHeading => size * 16 / 12,
                    _ => size * 13 / 12,
                };
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
        }
    }
}