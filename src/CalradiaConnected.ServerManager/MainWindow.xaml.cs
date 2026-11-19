using System.Windows;
using System.Windows.Controls;

namespace CoopServerManager
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void ConsoleTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                // Do not interrupt user selection when they are selecting/copying logs
                if (tb.SelectionLength == 0 && !tb.IsMouseCaptured && !tb.IsKeyboardFocused)
                {
                    tb.ScrollToEnd();
                }
            }
        }
    }
}
