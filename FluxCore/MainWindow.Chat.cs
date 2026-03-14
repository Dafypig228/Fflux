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
        // --- UI HANDLERS ---
        private void Btn_ShowLogs_Click(object sender, RoutedEventArgs e)
        {
            LogTextBox.Text = _voiceLog.ToString();
            LogOverlay.Visibility = Visibility.Visible;
            SettingsPanel.Visibility = Visibility.Collapsed;
        }

        private void Btn_CloseLogs_Click(object sender, RoutedEventArgs e) => LogOverlay.Visibility = Visibility.Collapsed;

        private void Btn_DebugDump_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Безопасное получение данных через null-check (?.)
                string meta = _cortex?.GetActiveWindow() ?? "N/A";
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_prompt.txt");
                File.WriteAllText(path, $"DEBUG DUMP:\nWindow: {meta}\nVoice Log:\n{_voiceLog}");

                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch { LogMessage("Could not save debug dump."); }
        }

        private async void Btn_DebugOCR_Click(object sender, RoutedEventArgs e)
        {
            string visual = _cortex != null ? await _cortex.GetVisualContext() : "Offline";
            string preview = visual.Length > 200 ? visual.Substring(0, 200) + "..." : visual;
            AddMessage($"[OCR VISION]:\n{preview}", false);
        }

        private void Btn_ResetAudio_Click(object sender, RoutedEventArgs e)
        {
            _ = _audioService?.Stop();
            _audioService?.Dispose();
            _audioService = new AudioService(_settings.ElevenLabsApiKey, _settings.SttLanguageCodes);
            _audioService.OnFinalText += async (text) => await ProcessRequest(text);
            _audioService.OnPartialText += (text) => Dispatcher.Invoke(() => StatusText.Text = text);
            _audioService.OnError += (err) => Dispatcher.Invoke(() => LogMessage($"Audio Error: {err}"));
            _audioService.OnConnected += () => Dispatcher.Invoke(() => LogMessage("ElevenLabs STT: Connected ✓"));
            if (_isMicOn) _ = _audioService.StartContinuousRecording();
            LogMessage("System: Audio Engine Restarted (ElevenLabs STT).");
        }

        private void Btn_Send_Click(object sender, RoutedEventArgs e)
        {
            if (InputBox != null && !string.IsNullOrWhiteSpace(InputBox.Text))
            {
                // Вызываем асинхронно, но в void методе можно не ждать (fire and forget)
                // Или можно сделать async void Btn_Send_Click
                _ = ProcessRequest(InputBox.Text);
                InputBox.Clear();
            }
        }
        private void InputBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Btn_Send_Click(sender, e); }

        // =========================================
        // TAB NAVIGATION
        // =========================================
        private void Tab_Chat_Click(object sender, RoutedEventArgs e)
        {
            ChatList.Visibility        = Visibility.Visible;
            MemoryPanel.Visibility     = Visibility.Collapsed;
            TasksPanel.Visibility      = Visibility.Collapsed;
            InnerVoicePanel.Visibility = Visibility.Collapsed;
            UpdateTabColors("chat");
        }

        private async void Tab_Memory_Click(object sender, RoutedEventArgs e)
        {
            ChatList.Visibility        = Visibility.Collapsed;
            MemoryPanel.Visibility     = Visibility.Visible;
            TasksPanel.Visibility      = Visibility.Collapsed;
            InnerVoicePanel.Visibility = Visibility.Collapsed;
            UpdateTabColors("memory");

            // Load memories
            if (_memory != null)
            {
                var memories = await _memory.GetRecentMemoriesAsync(50);
                MemoryList.ItemsSource = memories;
            }
        }

        private void Tab_Tasks_Click(object sender, RoutedEventArgs e)
        {
            ChatList.Visibility        = Visibility.Collapsed;
            MemoryPanel.Visibility     = Visibility.Collapsed;
            TasksPanel.Visibility      = Visibility.Visible;
            InnerVoicePanel.Visibility = Visibility.Collapsed;
            UpdateTabColors("tasks");

            // Load running tasks from brain
            if (_brain != null)
            {
                TasksList.ItemsSource = _brain.GetRunningTasks();
            }
        }

        private void UpdateTabColors(string activeTab)
        {
            var cyan = new SolidColorBrush(Color.FromRgb(0, 255, 209));
            var grey = new SolidColorBrush(Color.FromRgb(170, 170, 170));
            TabChat.Foreground       = activeTab == "chat"       ? cyan : grey;
            TabMemory.Foreground     = activeTab == "memory"     ? cyan : grey;
            TabTasks.Foreground      = activeTab == "tasks"      ? cyan : grey;
            TabInnerVoice.Foreground = activeTab == "innervoice" ? cyan : grey;
        }
        private void NameBox_TextChanged(object sender, TextChangedEventArgs e) { _panelName = NameBox.Text; TitleText.Text = _panelName.ToUpper(); }

        private void WakeWord_Changed(object sender, RoutedEventArgs e) { _requireWakeWord = WakeWordCheck.IsChecked == true; }
        private void Btn_NewPanel_Click(object sender, RoutedEventArgs e) { var p = new MainWindow(true, "Flux Unit"); p.Left = this.Left + 40; p.Top = this.Top + 40; p.Show(); }
        private void Btn_Mic_Click(object sender, RoutedEventArgs e) => ToggleMic(!_isMicOn);
        private void ToggleMic(bool state)
        {
            _isMicOn = state;

            if (_isMicOn)
            {
                // Start recording (fire-and-forget — errors handled inside AudioService)
                _ = _audioService?.StartContinuousRecording();

                // Визуал
                StatusText.Text = "🎤 LISTENING...";
                if (MainBorder != null)
                    MainBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 255, 209));
            }
            else
            {
                // ИСПРАВЛЕНИЕ: Так как Stop() возвращает Task, мы используем "_" (discard),
                // чтобы запустить его и не ждать (Fire-and-Forget).
                // Это уберет предупреждения и корректно остановит запись.
                _ = _audioService?.Stop();

                // Визуал
                StatusText.Text = "Processing...";
                if (MainBorder != null)
                    MainBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
            }

            UpdateMicButtonVisuals();
        }
        private void UpdateMicButtonVisuals() { if (BtnMic == null) return; BtnMic.Content = _isMicOn ? "🎤 ON" : "MIC OFF"; BtnMic.Foreground = _isMicOn ? new SolidColorBrush(Color.FromRgb(0, 255, 209)) : Brushes.Gray; }
        private void Btn_Exit_Click(object sender, RoutedEventArgs e) { if (!_isSecondary) Environment.Exit(0); else this.Close(); }

        // Add a message to the chat (user or AI only — not debug/status)
        private void AddMessage(string text, bool isUser)
        {
            Messages.Add(new ChatMessage { Text = text, IsUser = isUser });
            ScrollToBottom();
        }

        // Route debug/status info to the log panel only, never the chat
        private void LogMessage(string text)
        {
            _voiceLog.AppendLine($"[{DateTime.Now:HH:mm:ss}] {text}");
            if (LogOverlay.Visibility == Visibility.Visible)
                LogTextBox.Text = _voiceLog.ToString();
        }

        // --- НОВЫЙ МЕТОД: ПЕЧАТНАЯ МАШИНКА ---
        private async Task StreamMessage(string fullText)
        {
            var botMsg = new ChatMessage { Text = "", IsUser = false };
            Messages.Add(botMsg);

            // Скролл вниз
            if (VisualTreeHelper.GetChildrenCount(ChatList) > 0)
            {
                var border = VisualTreeHelper.GetChild(ChatList, 0) as Border;
                var scroller = VisualTreeHelper.GetChild(border, 0) as ScrollViewer;
                scroller?.ScrollToBottom();
            }

            // АДАПТИВНАЯ СКОРОСТЬ
            // Если текст длинный (логи), печатаем огромными кусками
            int totalLen = fullText.Length;
            int batchSize = totalLen > 1000 ? 100 : (totalLen > 200 ? 10 : 3);
            int delay = totalLen > 1000 ? 1 : 10;

            StringBuilder sb = new StringBuilder();

            for (int i = 0; i < totalLen; i += batchSize)
            {
                int len = Math.Min(batchSize, totalLen - i);
                sb.Append(fullText.Substring(i, len));
                botMsg.Text = sb.ToString();

                // Скролл реже
                if (i % (batchSize * 5) == 0 && VisualTreeHelper.GetChildrenCount(ChatList) > 0)
                {
                    var border = VisualTreeHelper.GetChild(ChatList, 0) as Border;
                    var scroller = VisualTreeHelper.GetChild(border, 0) as ScrollViewer;
                    scroller?.ScrollToBottom();
                }

                await Task.Delay(delay);
            }

            // Финальная синхронизация
            botMsg.Text = fullText;
            if (VisualTreeHelper.GetChildrenCount(ChatList) > 0)
            {
                var border = VisualTreeHelper.GetChild(ChatList, 0) as Border;
                var scroller = VisualTreeHelper.GetChild(border, 0) as ScrollViewer;
                scroller?.ScrollToBottom();
            }
        }


        private void ScrollToBottom()
        {
            if (VisualTreeHelper.GetChildrenCount(ChatList) > 0)
            {
                var border = VisualTreeHelper.GetChild(ChatList, 0) as Border;
                if (border != null)
                {
                    var scrollViewer = VisualTreeHelper.GetChild(border, 0) as ScrollViewer;
                    scrollViewer?.ScrollToBottom();
                }
            }
        }
    }
}
