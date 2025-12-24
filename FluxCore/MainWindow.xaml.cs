using FluxCore;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Runtime.InteropServices;

namespace FluxCore
{
    public class ChatMessage { public string Text { get; set; } = ""; public bool IsUser { get; set; } }

    public class AppConfig
    {
        public double R { get; set; } = 16;
        public double G { get; set; } = 16;
        public double B { get; set; } = 18;
        public double Alpha { get; set; } = 230;
        public string Name { get; set; } = "Flux ai";
        public bool RequireWakeWord { get; set; } = true;
    }

    public partial class MainWindow : Window
    {
        private AudioService? _audioService;
        private ScreenService? _screenService;
        private ContextService? _contextService;
        private GeminiService? _geminiService;

        private bool _isProcessing = false;
        private bool _isSecondary = false;
        private bool _isMicOn = false;
        private bool _requireWakeWord = true;
        private string _panelName = "Flux ai";

        private string _debugLastMeta = "";
        private string _debugLastOCR = "";
        private StringBuilder _voiceLog = new StringBuilder();

        public ObservableCollection<ChatMessage> Messages { get; set; } = new ObservableCollection<ChatMessage>();

        // --- WINAPI ДЛЯ HOTKEY И ALT+TAB ---
        private const int HOTKEY_ID = 9000;
        private const uint MOD_ALT = 0x0001;
        private const uint VK_F = 0x46;

        // Константы для скрытия из Alt+Tab
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        public MainWindow() : this(false, "Flux ai") { }

        public MainWindow(bool isSecondary, string startName = "Flux ai")
        {
            InitializeComponent();
            _isSecondary = isSecondary;
            _panelName = startName;

            NameBox.Text = _panelName;
            TitleText.Text = _panelName.ToUpper();

            this.DataContext = this;
            ChatList.ItemsSource = Messages;
            this.Loaded += OnWindowLoaded;
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            // 1. ПРИМЕНЯЕМ СТИЛЬ TOOLWINDOW (Убирает из Alt+Tab)
            HideFromAltTab();

            if (!_isSecondary)
            {
                var helper = new WindowInteropHelper(this);
                var source = HwndSource.FromHwnd(helper.Handle);
                source.AddHook(HwndHook);
                RegisterHotKey(helper.Handle, HOTKEY_ID, MOD_ALT, VK_F);

                var screenWidth = SystemParameters.PrimaryScreenWidth;
                this.Left = (screenWidth - this.Width) / 2;
                this.Top = 50;
            }
            else
            {
                if (_panelName == "Flux ai")
                {
                    _panelName = $"Flux Unit-{new Random().Next(10, 99)}";
                    NameBox.Text = _panelName;
                    TitleText.Text = _panelName.ToUpper();
                }
                FadeIn();
            }

            InitializeServices();
            LoadConfig();
            ApplyColors();
            UpdateMicButtonVisuals();

            if (!_isSecondary) FadeIn();
        }

        // --- ЛОГИКА СКРЫТИЯ ИЗ ALT+TAB ---
        private void HideFromAltTab()
        {
            var helper = new WindowInteropHelper(this);
            int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
            SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
        }

        private void InitializeServices()
        {
            try
            {
                _contextService = new ContextService();
                _screenService = new ScreenService();
                _geminiService = new GeminiService("AIzaSyDcSz3EBGyUT1NRkMwDzNfEFQk_8KfWFQs");

                _audioService = new AudioService();
                _audioService.OnFinalText += async (text) => await ProcessRequest(text);
                _audioService.OnError += (err) => Dispatcher.Invoke(() => AddMessage($"Audio Error: {err}", false));

                if (_isMicOn) _audioService.StartContinuousRecording();
                if (!_isSecondary) AddMessage($"Flux Core Ready.", false);
            }
            catch (Exception ex) { AddMessage($"Init Failed: {ex.Message}", false); }
        }

        private async Task ProcessRequest(string userVoice)
        {
            if (string.IsNullOrWhiteSpace(userVoice) || userVoice.Length < 2) return;

            _voiceLog.AppendLine($"[{DateTime.Now:HH:mm:ss}] {userVoice}");

            string cleanVoice = userVoice.Trim();

            if (_requireWakeWord && !InputBox.IsKeyboardFocused)
            {
                bool nameCalled = cleanVoice.StartsWith(_panelName, StringComparison.OrdinalIgnoreCase);
                if (!nameCalled) return;
                cleanVoice = cleanVoice.Substring(_panelName.Length).Trim();
                if (string.IsNullOrEmpty(cleanVoice)) return;
            }

            if (_isProcessing) return;
            _isProcessing = true;

            Dispatcher.Invoke(() => AddMessage(cleanVoice, true));
            if (!_isSecondary) Dispatcher.Invoke(() => StatusText.Text = "Analyzing...");

            try
            {
                if (_contextService == null || _screenService == null || _geminiService == null)
                    throw new Exception("Services Not Initialized");

                string meta = _contextService.GetLayer1_Metadata(out bool chg);
                string ui = _contextService.GetLayer3_UIHierarchy();
                var ocr = await _screenService.GetLayer2_OCR_WithDelta();
                string screenText = ocr.VisualChanged ? ocr.Text : "[No Visual Changes]";

                _debugLastMeta = $"Meta: {meta}\nUI: {ui}";
                _debugLastOCR = screenText;

                string answer = await _geminiService.AskContextAware(cleanVoice, meta, ui, screenText);

                Dispatcher.Invoke(() => {
                    AddMessage(answer, false);
                    StatusText.Text = "Idle";
                });
            }
            catch (Exception ex) { Dispatcher.Invoke(() => AddMessage($"Error: {ex.Message}", false)); }

            _isProcessing = false;
        }

        // --- UI HANDLERS ---

        private void Btn_ShowLogs_Click(object sender, RoutedEventArgs e)
        {
            LogTextBox.Text = _voiceLog.ToString();
            LogOverlay.Visibility = Visibility.Visible;
            SettingsPanel.Visibility = Visibility.Collapsed;
        }

        private void Btn_CloseLogs_Click(object sender, RoutedEventArgs e)
        {
            LogOverlay.Visibility = Visibility.Collapsed;
        }

        private void Btn_DebugDump_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dump = $"--- FLUX CORE DEBUG DUMP ---\nTime: {DateTime.Now}\nPanel: {_panelName}\n\n[WINDOW CONTEXT]\n{_debugLastMeta}\n\n[SCREEN OCR]\n{_debugLastOCR}\n";
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_prompt.txt");
                File.WriteAllText(path, dump);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch { AddMessage("Could not save debug dump.", false); }
        }

        private void Btn_DebugOCR_Click(object sender, RoutedEventArgs e)
        {
            string preview = _debugLastOCR.Length > 200 ? _debugLastOCR.Substring(0, 200) + "..." : _debugLastOCR;
            AddMessage($"[OCR DATA]:\n{preview}", false);
        }

        private void Btn_ResetAudio_Click(object sender, RoutedEventArgs e)
        {
            _audioService?.Stop();
            _audioService?.Dispose();
            _audioService = new AudioService();
            _audioService.OnFinalText += async (text) => await ProcessRequest(text);
            if (_isMicOn) _audioService.StartContinuousRecording();
            AddMessage("System: Audio Engine Restarted.", false);
        }

        private void Btn_Send_Click(object sender, RoutedEventArgs e) { if (!string.IsNullOrWhiteSpace(InputBox.Text)) { ProcessRequest(InputBox.Text); InputBox.Clear(); } }
        private void InputBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Btn_Send_Click(sender, e); }
        private void NameBox_TextChanged(object sender, TextChangedEventArgs e) { _panelName = NameBox.Text; TitleText.Text = _panelName.ToUpper(); }

        private void WakeWord_Changed(object sender, RoutedEventArgs e)
        {
            _requireWakeWord = WakeWordCheck.IsChecked == true;
        }

        private void Btn_NewPanel_Click(object sender, RoutedEventArgs e)
        {
            var p = new MainWindow(true, "Flux Unit");
            p.Left = this.Left + 40; p.Top = this.Top + 40;
            p.Show();
        }

        private void Btn_Mic_Click(object sender, RoutedEventArgs e) => ToggleMic(!_isMicOn);
        private void ToggleMic(bool state) { _isMicOn = state; if (_isMicOn) _audioService?.StartContinuousRecording(); else _audioService?.Stop(); UpdateMicButtonVisuals(); }
        private void UpdateMicButtonVisuals() { if (BtnMic == null) return; BtnMic.Content = _isMicOn ? "🎤 ON" : "MIC OFF"; BtnMic.Foreground = _isMicOn ? new SolidColorBrush(Color.FromRgb(0, 255, 209)) : Brushes.Gray; }

        private void Btn_Exit_Click(object sender, RoutedEventArgs e)
        {
            if (!_isSecondary) Environment.Exit(0);
            else this.Close();
        }

        private void AddMessage(string text, bool isUser)
        {
            Messages.Add(new ChatMessage { Text = text, IsUser = isUser });
            if (VisualTreeHelper.GetChildrenCount(ChatList) > 0)
            {
                var border = VisualTreeHelper.GetChild(ChatList, 0) as Border;
                if (border != null) { var sv = VisualTreeHelper.GetChild(border, 0) as ScrollViewer; sv?.ScrollToBottom(); }
            }
        }

        private void ApplyColors()
        {
            if (SliderR == null || TintBrush == null) return;
            TintBrush.Color = Color.FromArgb((byte)SliderAlpha.Value, (byte)SliderR.Value, (byte)SliderG.Value, (byte)SliderB.Value);
        }
        private void ColorSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (IsLoaded) ApplyColors(); }
        private void ToggleSettings_Click(object sender, RoutedEventArgs e) => SettingsPanel.Visibility = (SettingsPanel.Visibility == Visibility.Visible) ? Visibility.Collapsed : Visibility.Visible;
        private void OnDrag(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) this.DragMove(); }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) { if (msg == 0x0312 && wParam.ToInt32() == HOTKEY_ID) { ToggleAllPanels(); handled = true; } return IntPtr.Zero; }
        private void ToggleAllPanels() { bool show = (this.Visibility != Visibility.Visible || this.Opacity < 0.1); foreach (Window w in Application.Current.Windows) { if (w is MainWindow mw) { if (show) { mw.Opacity = 0; mw.Visibility = Visibility.Visible; mw.FadeIn(); } else mw.FadeOut(); } } if (show) { this.Activate(); InputBox.Focus(); } }
        public void FadeIn() { BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))); }
        public void FadeOut() { var a = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200)); a.Completed += (s, e) => Visibility = Visibility.Hidden; BeginAnimation(OpacityProperty, a); }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists("config.json"))
                {
                    var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText("config.json"));
                    if (cfg != null)
                    {
                        SliderR.Value = cfg.R; SliderG.Value = cfg.G; SliderB.Value = cfg.B; SliderAlpha.Value = cfg.Alpha;
                        _panelName = cfg.Name ?? "Flux ai"; NameBox.Text = _panelName; TitleText.Text = _panelName.ToUpper();
                        _requireWakeWord = cfg.RequireWakeWord;
                        WakeWordCheck.IsChecked = _requireWakeWord;
                    }
                }
            }
            catch { }
        }
        private void SaveConfig()
        {
            if (!_isSecondary) try
                {
                    File.WriteAllText("config.json", JsonSerializer.Serialize(new AppConfig
                    {
                        R = SliderR.Value,
                        G = SliderG.Value,
                        B = SliderB.Value,
                        Alpha = SliderAlpha.Value,
                        Name = _panelName,
                        RequireWakeWord = _requireWakeWord
                    }));
                }
                catch { }
        }
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e) { if (!_isSecondary) { UnregisterHotKey(new WindowInteropHelper(this).Handle, HOTKEY_ID); SaveConfig(); } _audioService?.Dispose(); }
    }
}