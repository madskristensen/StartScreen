using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace StartScreen.Models
{
    /// <summary>
    /// Represents a suggested Visual Studio extension.
    /// </summary>
    public class SuggestedExtension : INotifyPropertyChanged
    {
        private bool _isInstalled;

        /// <summary>
        /// The extension identifier (matches the registry value name prefix before the comma).
        /// Example: "MadsKristensen.EditorConfig"
        /// </summary>
        [JsonPropertyName("id")]
        public string Id { get; set; }

        /// <summary>
        /// Display name of the extension.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>
        /// Short description of what the extension does.
        /// </summary>
        [JsonPropertyName("description")]
        public string Description { get; set; }

        /// <summary>
        /// URL to the extension's Visual Studio Marketplace page.
        /// </summary>
        [JsonPropertyName("marketplaceUrl")]
        public string MarketplaceUrl { get; set; }

        /// <summary>
        /// Runtime property indicating whether this extension is currently installed.
        /// Not serialized from JSON - set at runtime via registry check.
        /// </summary>
        [JsonIgnore]
        public bool IsInstalled
        {
            get => _isInstalled;
            set
            {
                if (_isInstalled != value)
                {
                    _isInstalled = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
