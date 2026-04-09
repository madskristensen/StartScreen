namespace StartScreen.Models
{
    /// <summary>
    /// Represents a recently used project template.
    /// </summary>
    public class RecentTemplate
    {
        public string Name { get; set; }
        public string TemplateId { get; set; }
        public string Language { get; set; }
        public string Platform { get; set; }

        public override string ToString()
        {
            return Name ?? TemplateId ?? "Unknown Template";
        }
    }
}
