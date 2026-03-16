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
            UpdateScriptGlobals(); // Expose new Telegram instance to RUN_CSHARP scripts

            // Restore saved chat filter (empty = all DMs + groups, no channels)
            if (_settings.TelegramChatIds?.Count > 0)
                _telegram.AllowedChatIds = new System.Collections.Generic.HashSet<long>(_settings.TelegramChatIds);

            // Route WTelegramClient's own internal logs (level >= 2 = info/warning/error) to the Logs panel.
            // This reveals exactly what WTelegramClient is doing: DC selection, key exchange, auth state, etc.
            WTelegram.Helpers.Log = (level, msg) =>
            {
                if (level >= 2) // 0=verbose 1=debug 2=info 3=warning 4=error
                    Dispatcher.InvokeAsync(() => LogMessage($"[WTG] {msg.TrimEnd()}"));
            };

            // Route our own TelegramService log messages to the Logs panel
            _telegram.OnLog += (msg) => Dispatcher.InvokeAsync(() => LogMessage(msg));

            // WPF dialog for phone / verification-code / 2FA prompts
            _telegram.AuthPrompt = (prompt) =>
            {
                string result = "";
                Dispatcher.Invoke(() =>
                {
                    // Dialog is Topmost=True + CenterScreen — appears above the overlay
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

            // Set owner IDs for autonomous outgoing messages
            _telegram.OwnerChatId    = _settings.TelegramOwnerChatId;
            _telegram.OwnerChannelId = _settings.TelegramOwnerChannelId;

            _ = Task.Run(async () =>
            {
                await _telegram.StartAsync();
                Dispatcher.InvokeAsync(UpdateTelegramStatus);

                // Start Inner Voice after Telegram is connected (needs send capability)
                if (_settings.InnerVoiceEnabled
                    && (_settings.TelegramOwnerChatId != 0 || _settings.TelegramOwnerChannelId != 0)
                    && _telegram.IsConnected && _brain != null && _dataLake != null
                    && _coreMemory != null && _gemini != null && _cortex != null)
                {
                    Dispatcher.InvokeAsync(() => StartInnerVoice());
                }
            });

            UpdateTelegramStatus();
        }

        /// <summary>
        /// Rebuilds and assigns ScriptGlobals to JarvisCore so RUN_CSHARP scripts
        /// have access to all currently-initialized services.
        /// Call any time a service is (re-)initialized (e.g., after Telegram reconnects).
        /// </summary>
        internal void UpdateScriptGlobals()
        {
            if (_jarvis == null) return;
            _jarvis.ScriptGlobals = new ScriptGlobals
            {
                Telegram       = _telegram,
                DataLake       = _dataLake,
                KnowledgeGraph = _knowledgeGraph,
                Memory         = _memory,
                Settings       = _settings,
                Gemini         = _gemini,
            };
        }

        private void StartInnerVoice()
        {
            try
            {
                // Tear down any previous instance
                _innerVoice?.Dispose();
                _innerVoice = null;

                var state    = InnerVoice.InnerState.Load();
                var drives   = new InnerVoice.DrivesEngine(state);
                var privacy  = new InnerVoice.PrivacyFilter();
                var composer = new InnerVoice.TelegramComposer(_telegram!);
                var budget   = new InnerVoice.TokenBudget(_settings, state);
                var guard    = new InnerVoice.AntiSpamGuard(_settings, _cortex!);

                _innerVoice = new InnerVoice.InnerVoiceService(
                    _gemini!,        // Gemini 2.5-Flash directly for best personality fidelity
                    composer,
                    _dataLake!,
                    _coreMemory!,
                    _brain!,
                    drives,
                    guard,
                    budget,
                    privacy,
                    state);

                // Wire drive events to user interactions
                _brain!.OnMessage += (msg, isUser) =>
                {
                    if (isUser)
                    {
                        string? flushMsg = _innerVoice?.OnUserReturned();
                        if (!string.IsNullOrWhiteSpace(flushMsg))
                            Task.Run(() => composer.SendAsync(flushMsg));
                    }
                };

                // Wire Telegram new-message to curiosity boost
                _telegram!.OnNewMessage += kind => drives.OnNewObservation(kind);

                // Wire OnThought → visible thought feed (newest first, cap at 50)
                _innerVoice.OnThought += thought => Dispatcher.InvokeAsync(() =>
                {
                    InnerVoiceThoughts.Insert(0, new InnerThought
                    {
                        Time = DateTime.Now.ToString("HH:mm:ss"),
                        Text = thought
                    });
                    // Trim oldest entries from the BOTTOM (index Count-1) — newest are at index 0
                    while (InnerVoiceThoughts.Count > 50)
                        InnerVoiceThoughts.RemoveAt(InnerVoiceThoughts.Count - 1);
                });

                // Wire OnStatus → status dot + label
                _innerVoice.OnStatus += status => Dispatcher.InvokeAsync(() =>
                {
                    InnerStatusText.Text = status;
                    InnerStatusDot.Fill  = status switch
                    {
                        "Thinking"                              => new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)), // #FFD700 gold
                        "Sending"                               => new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0xD1)), // #00FFD1 cyan
                        var s when s.StartsWith("Researching")  => new SolidColorBrush(Color.FromRgb(0xFF, 0x99, 0x44)), // #FF9944 orange
                        _                                       => new SolidColorBrush(Color.FromArgb(64, 255, 255, 255))
                    };
                });

                // Wire OnDrivesChanged → 4 drive ProgressBars
                _innerVoice.OnDrivesChanged += snap => Dispatcher.InvokeAsync(() =>
                {
                    GaugeBoredom.Value     = snap.Boredom;
                    GaugeCuriosity.Value   = snap.Curiosity;
                    GaugeFrustration.Value = snap.Frustration;
                    GaugeEnergy.Value      = snap.Energy;
                });

                _innerVoice.Start();
                System.Diagnostics.Debug.WriteLine("[InnerVoice] Inner Voice started");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[InnerVoice] Failed to start: {ex.Message}");
            }
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

            // Enable the "⚙ Chats" button only while connected
            if (TgChooseChatsBtn != null)
                TgChooseChatsBtn.IsEnabled = _telegram?.IsConnected == true;
        }

        /// <summary>
        /// Opens the chat picker dialog so the user can choose which chats Davos monitors.
        /// Available only while Telegram is connected.
        /// </summary>
        private async void TgChooseChats_Click(object sender, RoutedEventArgs e)
        {
            if (_telegram == null || !_telegram.IsConnected) return;

            TgChooseChatsBtn.IsEnabled = false;
            LogMessage("[Telegram] Fetching chat list…");

            var chats = await Task.Run(() => _telegram.GetAvailableChatsAsync());

            TgChooseChatsBtn.IsEnabled = true;

            if (chats.Count == 0)
            {
                LogMessage("[Telegram] No chats found.");
                return;
            }

            var dlg = new TelegramChatPickerDialog(chats, _telegram.AllowedChatIds);
            if (dlg.ShowDialog() != true) return;

            // Apply and persist the selection
            _telegram.AllowedChatIds   = dlg.SelectedIds;
            _settings.TelegramChatIds  = dlg.SelectedIds.ToList();
            _settings.Save();

            string summary = dlg.SelectedIds.Count == 0
                ? "all DMs and groups (no filter)"
                : $"{dlg.SelectedIds.Count} chat(s) selected";
            LogMessage($"[Telegram] Now monitoring: {summary}");
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

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e) { if (!_isSecondary) { UnregisterHotKey(new WindowInteropHelper(this).Handle, HOTKEY_ID); SaveConfig(); } _audioService?.Dispose(); _ = _tts?.DisposeAsync().AsTask(); _ = _executor?.DisposeAsync(); _clipboardService?.Dispose(); _fileWatcher?.Dispose(); _gitWatcher?.Dispose(); _metrics?.Dispose(); _notifications?.Dispose(); _chromeBridge?.Dispose(); _eventLog?.Dispose(); _innerVoice?.Dispose(); _telegram?.Dispose(); _knowledgeGraph?.Dispose(); _dataLake?.Dispose(); }
    }
}
