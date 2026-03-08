using Application = System.Windows.Application;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;
using Clipboard = System.Windows.Clipboard;

using FluxCore;
using FluxCore.LLM;
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
using System.ComponentModel;

namespace FluxCore
{
    public partial class MainWindow
    {
        // =========================================
        // SETTINGS HANDLERS
        // =========================================
        private AppSettings _settings = new AppSettings();
        private bool _loadingConfig = false;

        private void SliderOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || _loadingConfig) return;

            // OPACITY FIX: Change background alpha instead of window opacity
            // Window opacity doesn't work well with AllowsTransparency="True"
            byte alpha = (byte)(SliderOpacity.Value * 255);
            TintBrush.Color = Color.FromArgb(alpha, 16, 16, 21);

            _settings.WindowOpacity = SliderOpacity.Value;
            _settings.Save();
        }

        private void SliderBlur_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || _loadingConfig) return;
            BackgroundBlur.Radius = SliderBlur.Value;
            _settings.BlurRadius = SliderBlur.Value;
            _settings.Save();
        }

        private void AutoMinimize_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _loadingConfig) return;
            _settings.AutoMinimizeOnComplete = AutoMinimizeCheck.IsChecked == true;
            _settings.Save();
        }

        private void ElevenLabsKey_Changed(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded || _loadingConfig) return;
            _settings.ElevenLabsApiKey = ElevenLabsKeyBox.Text.Trim();
            _settings.Save();
        }

        private void SttLang_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _loadingConfig) return;
            _settings.SttRussian = SttRuCheck.IsChecked == true;
            _settings.SttEnglish = SttEnCheck.IsChecked == true;
            _settings.SttKazakh  = SttKkCheck.IsChecked == true;
            _settings.Save();
        }

        private void Tts_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _loadingConfig) return;
            _settings.TtsEnabled = TtsCheck.IsChecked == true;
            _settings.Save();

            if (_settings.TtsEnabled && _tts == null)
            {
                _tts = new GeminiTtsService(API_KEY, _settings.TtsVoice);
                _tts.OnError += (err) => Dispatcher.InvokeAsync(() => LogMessage($"[TTS Error] {err}"));
                _ = _tts.ConnectAsync();
                _brain!.OnMessage += (text, isUser) => { if (!isUser) _ = _tts.SpeakAsync(text); };
            }
            else if (!_settings.TtsEnabled && _tts != null)
            {
                _tts.StopPlayback();
                _ = _tts.DisposeAsync().AsTask();
                _tts = null;
            }
        }

        // =========================================
        // TELEGRAM SETTINGS
        // =========================================

        private void TelegramCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _loadingConfig) return;
            _settings.TelegramEnabled = TelegramCheck.IsChecked == true;
            TelegramFields.Visibility = _settings.TelegramEnabled ? Visibility.Visible : Visibility.Collapsed;
            _settings.Save();
        }

        private void TgApiId_Changed(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded || _loadingConfig) return;
            if (int.TryParse(TgApiIdBox.Text.Trim(), out int id)) _settings.TelegramApiId = id;
            _settings.Save();
        }

        private void TgApiHash_Changed(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded || _loadingConfig) return;
            _settings.TelegramApiHash = TgApiHashBox.Text.Trim();
            _settings.Save();
        }

        /// <summary>
        /// (Re-)initialises TelegramService with the current settings.
        /// Safe to call at startup and after a session reset.
        /// </summary>
        internal void InitializeTelegram()
        {
            // Tear down any existing connection first
            _telegram?.Dispose();
            _telegram        = null;
            _jarvis.Telegram = null;

            if (!_settings.TelegramEnabled
                || _settings.TelegramApiId <= 0
                || string.IsNullOrEmpty(_settings.TelegramApiHash))
            {
                UpdateTelegramStatus();
                return;
            }

            _telegram          = new TelegramService(_settings.TelegramApiId, _settings.TelegramApiHash);
            _telegram.DataLake = _dataLake;
            _telegram.Memory   = _memory;
            _jarvis.Telegram   = _telegram;

            // WPF dialog for phone / verification-code / 2FA prompts
            _telegram.AuthPrompt = (prompt) =>
            {
                string result = "";
                Dispatcher.Invoke(() =>
                {
                    LogMessage($"[Telegram] Auth prompt: {prompt}");
                    // Dialog is Topmost=True + CenterScreen so it appears above the overlay
                    var dlg = new TelegramAuthDialog(prompt);
                    dlg.Activate();
                    if (dlg.ShowDialog() == true) result = dlg.Answer;
                });
                return result;
            };

            // Route Telegram errors to the Logs panel so the full message is visible
            _telegram.OnError += (err) => Dispatcher.InvokeAsync(() =>
            {
                LogMessage(err);
                UpdateTelegramStatus();
            });

            _ = Task.Run(async () =>
            {
                await _telegram.StartAsync();
                Dispatcher.InvokeAsync(UpdateTelegramStatus);
            });

            UpdateTelegramStatus();
        }

        /// <summary>
        /// Disposes the current client, deletes the session data file (telegram.dat),
        /// then reconnects from scratch. The WPF auth dialog will appear for re-auth.
        /// No GC tricks needed: the stream-based session approach has no file-lock issues.
        /// </summary>
        private async void TgResetSession_Click(object sender, RoutedEventArgs e)
        {
            // 1. Tear down existing connection
            _telegram?.Dispose();
            _telegram        = null;
            _jarvis.Telegram = null;
            UpdateTelegramStatus();

            // 2. Brief pause to ensure Dispose has finished
            await Task.Delay(300);

            // 3. Delete session data so next connect starts fresh (auth dialog will appear)
            try
            {
                string davosDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Davos");

                if (Directory.Exists(davosDir))
                    foreach (var file in Directory.GetFiles(davosDir, "telegram*"))
                        try { File.Delete(file); } catch { }
            }
            catch { }

            // 4. Reconnect — auth dialog will appear for phone → code → 2FA
            InitializeTelegram();
        }

        /// <summary>
        /// Updates the Telegram status indicator in the settings panel.
        /// </summary>
        internal void UpdateTelegramStatus()
        {
            if (TelegramStatus == null) return;
            if (_telegram == null)
            {
                TelegramStatus.Text = "● Not configured";
                TelegramStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x50, 0x60, 0x60));
            }
            else if (_telegram.IsConnected)
            {
                TelegramStatus.Text = $"● Connected ({_telegram.MessageCount} msgs)";
                TelegramStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x00, 0xCC, 0x88));
            }
            else
            {
                TelegramStatus.Text = $"● {_telegram.StatusMessage}";
                TelegramStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xFF, 0xAA, 0x00));
            }
        }

        private void Btn_Minimize_Click(object sender, RoutedEventArgs e) => this.WindowState = WindowState.Minimized;

        private void Btn_ClearChat_Click(object sender, RoutedEventArgs e)
        {
            Messages.Clear();
            SaveSession();
            AddMessage("Chat cleared.", false);
        }

        private void ToggleSettings_Click(object sender, RoutedEventArgs e) => SettingsPanel.Visibility = (SettingsPanel.Visibility == Visibility.Visible) ? Visibility.Collapsed : Visibility.Visible;
        private void OnDrag(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) this.DragMove(); }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) { if (msg == 0x0312 && wParam.ToInt32() == HOTKEY_ID) { ToggleAllPanels(); handled = true; } return IntPtr.Zero; }
        private void ToggleAllPanels() { bool show = (this.Visibility != Visibility.Visible || this.Opacity < 0.1 || this.WindowState == WindowState.Minimized); foreach (Window w in Application.Current.Windows) { if (w is MainWindow mw) { if (show) { mw.WindowState = WindowState.Normal; mw.Opacity = 0; mw.Visibility = Visibility.Visible; mw.FadeIn(); } else mw.FadeOut(); } } if (show) { this.Activate(); InputBox.Focus(); } }
        public void FadeIn() { BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))); }
        public void FadeOut() { var a = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200)); a.Completed += (s, e) => Visibility = Visibility.Hidden; BeginAnimation(OpacityProperty, a); }
        public void ForceHide() { Dispatcher.Invoke(() => FadeOut()); }

        private void LoadConfig()
        {
            _loadingConfig = true;
            try
            {
                _settings = AppSettings.Load();

                // Setting slider values triggers the change handlers which apply the visual effects
                // (SliderOpacity_Changed sets TintBrush alpha, SliderBlur_Changed sets BackgroundBlur.Radius)
                // Do NOT also set this.Opacity or BackgroundBlur.Radius here — that would double-apply
                SliderOpacity.Value = _settings.WindowOpacity;
                SliderBlur.Value = _settings.BlurRadius;

                _panelName = _settings.WakeWord;
                NameBox.Text = _panelName;
                TitleText.Text = _panelName.ToUpper();
                _requireWakeWord = _settings.RequireWakeWord;
                WakeWordCheck.IsChecked = _requireWakeWord;
                AutoMinimizeCheck.IsChecked = _settings.AutoMinimizeOnComplete;
                TtsCheck.IsChecked = _settings.TtsEnabled;
                ElevenLabsKeyBox.Text = _settings.ElevenLabsApiKey;
                SttRuCheck.IsChecked = _settings.SttRussian;
                SttEnCheck.IsChecked = _settings.SttEnglish;
                SttKkCheck.IsChecked = _settings.SttKazakh;
                TelegramCheck.IsChecked = _settings.TelegramEnabled;
                TgApiIdBox.Text = _settings.TelegramApiId > 0 ? _settings.TelegramApiId.ToString() : "";
                TgApiHashBox.Text = _settings.TelegramApiHash;
                TelegramFields.Visibility = _settings.TelegramEnabled ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
            finally { _loadingConfig = false; }
        }

        private void SaveConfig()
        {
            if (!_isSecondary)
            {
                try
                {
                    _settings.WakeWord = _panelName;
                    _settings.RequireWakeWord = _requireWakeWord;
                    _settings.Save();
                }
                catch { }
            }
        }

        private void ApplyColors()
        {
            // Apply theme colors from settings
            // Currently using default cyan accent color
            try
            {
                // Default neon colors are already set in XAML
                // This method exists for future theming support
            }
            catch { }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e) { if (!_isSecondary) { UnregisterHotKey(new WindowInteropHelper(this).Handle, HOTKEY_ID); SaveConfig(); } _audioService?.Dispose(); _ = _tts?.DisposeAsync().AsTask(); _ = _executor?.DisposeAsync(); _clipboardService?.Dispose(); _fileWatcher?.Dispose(); _gitWatcher?.Dispose(); _metrics?.Dispose(); _notifications?.Dispose(); _chromeBridge?.Dispose(); _eventLog?.Dispose(); _telegram?.Dispose(); _knowledgeGraph?.Dispose(); _dataLake?.Dispose(); }
    }
}
