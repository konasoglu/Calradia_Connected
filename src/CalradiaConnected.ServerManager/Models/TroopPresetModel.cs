namespace CoopServerManager.Models
{
    public class TroopPresetModel
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Culture { get; set; } = string.Empty;
        public string Tier { get; set; } = string.Empty;
        public string DisplayName => $"{Name} ({Tier} - {Culture})";

        public TroopPresetModel(string id, string name, string culture, string tier)
        {
            Id = id;
            Name = name;
            Culture = culture;
            Tier = tier;
        }
    }
}
