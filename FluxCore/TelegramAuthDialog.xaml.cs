using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace FluxCore
{
    /// <summary>
    /// Simple input dialog for Telegram auth prompts (phone number, verification code, 2FA password).
    /// Shown when WTelegramClient needs user input during first-run authentication.
    /// </summary>
    public partial class TelegramAuthDialog : Window
    {
        public string Answer { get; private set; } = "";

        public TelegramAuthDialog(string prompt)
        {
            InitializeComponent();
            PromptText.Text = prompt;
            AnswerBox.Focus();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Answer = AnswerBox.Text.Trim();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void AnswerBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                Answer = AnswerBox.Text.Trim();
                DialogResult = true;
            }
            else if (e.Key == Key.Escape)
            {
                DialogResult = false;
            }
        }
    }
}
