using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using CheckBox = System.Windows.Controls.CheckBox;

namespace FluxCore
{
    /// <summary>
    /// Lists all Telegram chats/DMs/channels with checkboxes so the user can pick
    /// which ones Davos monitors. Launched from the "⚙ Chats" button in Settings.
    /// Empty selection = monitor all DMs and groups (default behaviour).
    /// </summary>
    public partial class TelegramChatPickerDialog : Window
    {
        /// <summary>IDs the user confirmed. Empty = monitor everything (unfiltered).</summary>
        public HashSet<long> SelectedIds { get; private set; } = new();

        private readonly List<(CheckBox Box, long Id)> _items = new();

        public TelegramChatPickerDialog(
            List<TelegramService.TgChatInfo> chats,
            HashSet<long> currentSelection)
        {
            InitializeComponent();

            // Sort: DMs first, then Groups, then Channels; alphabetical within each group
            foreach (var chat in chats.OrderBy(c => c.Type == "DM" ? 0 : c.Type == "Group" ? 1 : 2)
                                      .ThenBy(c => c.Name))
            {
                bool isChecked = currentSelection.Count > 0 && currentSelection.Contains(chat.Id);

                string typeTag = chat.Type switch
                {
                    "DM"      => "DM     ",
                    "Group"   => "Group  ",
                    "Channel" => "Channel",
                    _         => chat.Type
                };

                var cb = new CheckBox
                {
                    Content    = $"[{typeTag}] {chat.Name}",
                    IsChecked  = isChecked,
                    Foreground = chat.Type == "DM"
                                    ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xDD, 0xFF))
                                    : new SolidColorBrush(Colors.White),
                    Margin     = new Thickness(2, 3, 2, 3),
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize   = 10.5,
                    Tag        = chat.Id
                };

                _items.Add((cb, chat.Id));
                ChatList.Items.Add(cb);
            }
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var (box, _) in _items) box.IsChecked = true;
        }

        private void DeselectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var (box, _) in _items) box.IsChecked = false;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            SelectedIds = new HashSet<long>(
                _items.Where(x => x.Box.IsChecked == true).Select(x => x.Id));
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
