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
        // Флаг, чтобы понимать, что микрофон включен именно удержанием кнопки
        private bool _isPttActive = false;

        // 2. ИЗМЕНЕНИЕ: Обработка нажатия (ВКЛЮЧИТЬ МИКРОФОН)
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // 3. Enter для отправки текста (если мы в поле ввода)
            if (e.Key == Key.Enter && InputBox.IsKeyboardFocused)
            {
                Btn_Send_Click(sender, e);
                e.Handled = true; // Чтобы не было звука "дзинь"
            }
        }

        // 3. ИЗМЕНЕНИЕ: Обработка отпускания (ВЫКЛЮЧИТЬ МИКРОФОН)
        private const int VK_RMENU = 0xA5; // Right Alt
        private void InputTimer_Tick(object? sender, EventArgs e)
        {
            // Check Right Alt (VK_RMENU)
            bool isHotkeyDown = (GetAsyncKeyState(VK_RMENU) & 0x8000) != 0;

            if (isHotkeyDown)
            {
                // Если нажали и микрофон еще не включен
                if (!_isMicOn && (_brain == null || !_brain.IsWorking))
                {
                    _isPttActive = true; // Запоминаем, что включили кнопкой
                    ToggleMic(true);
                }
            }
            else
            {
                // Если отпустили, и микрофон был включен именно кнопкой (PTT)
                if (_isPttActive)
                {
                    _isPttActive = false;
                    ToggleMic(false);
                }
            }
        }
        // --- РУКИ FLUX (ЗАПУСК ПРОГРАММ) ---
        private void ExecuteCommands(string aiResponse)
        {
            try
            {
                if (string.IsNullOrEmpty(aiResponse)) return;

                // 1. КОМАНДА ОТКРЫТИЯ: [[OPEN: ...]]
                if (aiResponse.Contains("[[OPEN:"))
                {
                    int start = aiResponse.IndexOf("[[OPEN:") + 7;
                    int end = aiResponse.IndexOf("]]", start);
                    if (end > start)
                    {
                        string target = aiResponse.Substring(start, end - start).Trim().ToLower();

                        // Специфичные алиасы
                        if (target == "chrome" || target == "browser")
                            Process.Start(new ProcessStartInfo("cmd", "/c start chrome") { CreateNoWindow = true });
                        else if (target == "notepad")
                            Process.Start("notepad.exe");
                        else if (target == "calc")
                            Process.Start("calc.exe");
                        else
                        {
                            // Пытаемся запустить как системную команду или URL
                            Process.Start(new ProcessStartInfo("cmd", $"/c start {target}") { CreateNoWindow = true });
                        }
                    }
                }

                // 2. КОМАНДА ПОИСКА: [[SEARCH: ...]]
                if (aiResponse.Contains("[[SEARCH:"))
                {
                    int start = aiResponse.IndexOf("[[SEARCH:") + 9;
                    int end = aiResponse.IndexOf("]]", start);
                    if (end > start)
                    {
                        string query = aiResponse.Substring(start, end - start).Trim();
                        string url = $"https://www.google.com/search?q={Uri.EscapeDataString(query)}";
                        Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
                    }
                }
            }
            catch
            {
                // Если не получилось открыть, просто игнорируем, чтобы не крашить программу
            }
        }

        private async Task ProcessRequest(string userVoice)
        {
            if (string.IsNullOrWhiteSpace(userVoice)) return;
            if (_brain == null) return;

            // Stop TTS immediately when user speaks/types (interrupt mid-sentence)
            _tts?.StopPlayback();

            // FluxBrain handles everything: classification, routing, parallel execution
            // It never blocks, never drops requests
            await _brain.SubmitAsync(userVoice);
        }
    }
}
