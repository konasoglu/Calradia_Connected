namespace CoopServerManager.Models
{
    public class SettingOption<T>
    {
        public string Title { get; set; } = string.Empty;
        public T Value { get; set; } = default!;

        public SettingOption() { }

        public SettingOption(string title, T value)
        {
            Title = title;
            Value = value;
        }

        public override string ToString() => Title;
    }
}
