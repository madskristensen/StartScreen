using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;

namespace StartScreen.ToolWindows
{
    public enum TypographyRole
    {
        Inherit,
        Body,
        Metadata,
        Heading,
    }

    public static class Typography
    {
        public static readonly DependencyProperty EnvironmentSizeProperty = DependencyProperty.RegisterAttached(
            "EnvironmentSize", typeof(double), typeof(Typography), new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.Inherits));

        public static readonly DependencyProperty RoleProperty = DependencyProperty.RegisterAttached(
            "Role", typeof(TypographyRole), typeof(Typography), new PropertyMetadata(TypographyRole.Inherit, OnRoleChanged));

        private static readonly IValueConverter _fontSizeConverter = new FontSizeConverter();

        public static double GetEnvironmentSize(DependencyObject element) => (double)element.GetValue(EnvironmentSizeProperty);
        public static void SetEnvironmentSize(DependencyObject element, double value) => element.SetValue(EnvironmentSizeProperty, value);
        public static TypographyRole GetRole(DependencyObject element) => (TypographyRole)element.GetValue(RoleProperty);
        public static void SetRole(DependencyObject element, TypographyRole value) => element.SetValue(RoleProperty, value);

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
                    _ => size * 13 / 12,
                };
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
        }
    }
}