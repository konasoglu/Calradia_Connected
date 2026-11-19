using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CoopServerManager.Models
{
    public class ModuleInfoModel : INotifyPropertyChanged
    {
        private bool isSelected;

        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string DirectoryPath { get; set; } = string.Empty;
        public bool IsOfficial { get; set; }
        public bool IsCoopCore { get; set; }
        public bool IsCoreRequired { get; set; }
        public bool IsIncompatible { get; set; }
        public bool IsRequired => IsCoreRequired;
        public List<string> Dependencies { get; set; } = new();

        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (IsRequired)
                {
                    isSelected = true; // Required modules cannot be deselected
                }
                else
                {
                    isSelected = value;
                }
                OnPropertyChanged();
            }
        }

        public string BadgeText =>
            IsIncompatible ? "Uyumsuz (Coop Kapatır)" :
            (IsCoreRequired ? (IsCoopCore ? "Coop Çekirdeği" : "Temel Oyun") : "Topluluk / Özel Mod");

        public string CategoryColor =>
            IsIncompatible ? "#64748B" :
            (IsCoreRequired ? (IsCoopCore ? "#10B981" : "#3B82F6") : "#F59E0B");

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
