using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace StartScreen.Helpers
{
    /// <summary>
    /// Provides an attached property that parses markdown-style links in text
    /// and renders them as clickable <see cref="Hyperlink"/> inlines in a <see cref="TextBlock"/>.
    /// Syntax: <c>[link text](url)</c>.
    /// </summary>
    public static class InlineTextHelper
    {
        private static readonly Regex LinkPattern = new Regex(
            @"\[(?<text>[^\]]+)\]\((?<url>[^\)]+)\)",
            RegexOptions.Compiled);

        /// <summary>
        /// Identifies the <c>FormattedText</c> attached property.
        /// When set on a <see cref="TextBlock"/>, the value is parsed for
        /// markdown-style links (<c>[text](url)</c>) and rendered as a mix
        /// of plain <see cref="Run"/> and clickable <see cref="Hyperlink"/> inlines.
        /// </summary>
        public static readonly DependencyProperty FormattedTextProperty =
            DependencyProperty.RegisterAttached(
                "FormattedText",
                typeof(string),
                typeof(InlineTextHelper),
                new PropertyMetadata(null, OnFormattedTextChanged));

        public static string GetFormattedText(DependencyObject obj)
        {
            return (string)obj.GetValue(FormattedTextProperty);
        }

        public static void SetFormattedText(DependencyObject obj, string value)
        {
            obj.SetValue(FormattedTextProperty, value);
        }

        private static void OnFormattedTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TextBlock textBlock)
            {
                textBlock.Inlines.Clear();
                var text = e.NewValue as string;

                if (string.IsNullOrEmpty(text))
                {
                    return;
                }

                foreach (var inline in ParseInlines(text))
                {
                    textBlock.Inlines.Add(inline);
                }
            }
        }

        /// <summary>
        /// Parses text containing markdown-style links and returns a list of
        /// <see cref="Run"/> and <see cref="Hyperlink"/> inlines.
        /// </summary>
        internal static List<Inline> ParseInlines(string text)
        {
            var inlines = new List<Inline>();
            var lastIndex = 0;

            foreach (Match match in LinkPattern.Matches(text))
            {
                if (match.Index > lastIndex)
                {
                    inlines.Add(new Run(text.Substring(lastIndex, match.Index - lastIndex)));
                }

                var linkText = match.Groups["text"].Value;
                var url = match.Groups["url"].Value;

                try
                {
                    var uri = new Uri(url, UriKind.Absolute);
                    var hyperlink = new Hyperlink(new Run(linkText))
                    {
                        NavigateUri = uri
                    };
                    hyperlink.RequestNavigate += OnHyperlinkRequestNavigate;
                    inlines.Add(hyperlink);
                }
                catch (UriFormatException)
                {
                    // If the URL is malformed, render as plain text
                    inlines.Add(new Run(match.Value));
                }

                lastIndex = match.Index + match.Length;
            }

            if (lastIndex < text.Length)
            {
                inlines.Add(new Run(text.Substring(lastIndex)));
            }

            return inlines;
        }

        private static void OnHyperlinkRequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ex.Log();
            }

            e.Handled = true;
        }
    }
}
