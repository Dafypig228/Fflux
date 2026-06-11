using Application = System.Windows.Application;       // <--- ВОТ ЭТО ИСПРАВИТ ОШИБКУ
using Brushes = System.Windows.Media.Brushes;         // Исправляет Brushes
using Color = System.Windows.Media.Color;             // Исправляет Color
using KeyEventArgs = System.Windows.Input.KeyEventArgs; // Исправляет KeyEventArgs
using Point = System.Windows.Point;         // Используем WPF Point
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
        // --- NEURO-HUD METHODS ---
        private void UpdateHudState(string state)
        {
            NeuroHudPanel.Visibility = Visibility.Visible;
            if (StatusText != null) StatusText.Visibility = Visibility.Collapsed; // Hide old status

            HudStateText.Text = state;

            // Color coding
            if (state == "THINKING") HudStateLight.Fill = Brushes.Cyan;
            else if (state == "ACTING") HudStateLight.Fill = Brushes.Yellow;
            else if (state == "VERIFYING") HudStateLight.Fill = Brushes.Magenta;
            else if (state == "REFLECTING") HudStateLight.Fill = Brushes.Lime;

            // Reset transient badges
            HudValidationBadge.Visibility = Visibility.Collapsed;
        }

        private void UpdateHudContent(string thought)
        {
            HudContentText.Text = thought;
            HudActionBox.Visibility = Visibility.Collapsed;
        }

        private void UpdateHudAction(string action)
        {
            HudActionBox.Visibility = Visibility.Visible;
            HudActionText.Text = action;
        }

        private async void UpdateHudValidation(bool success, string reason)
        {
            HudValidationBadge.Visibility = Visibility.Visible;
            if (success)
            {
                (HudValidationBadge.Child as TextBlock).Text = "👁️ VERIFIED";
                (HudValidationBadge.Child as TextBlock).Foreground = Brushes.Lime;
                HudValidationBadge.Background = new SolidColorBrush(Color.FromArgb(50, 0, 255, 0));
            }
            else
            {
                (HudValidationBadge.Child as TextBlock).Text = "❌ REJECTED";
                (HudValidationBadge.Child as TextBlock).Foreground = Brushes.Red;
                HudValidationBadge.Background = new SolidColorBrush(Color.FromArgb(50, 255, 0, 0));
            }

            // Keep it visible for a moment
            await Task.Delay(2000);
        }

        private async void BackgroundMonitor_Tick(object sender, EventArgs e)
        {
            if (_cortex == null || _memory == null) return;

            try
            {
                string currentWindow = _cortex.GetActiveWindow();

                // Проверяем смену окна
                if (!string.IsNullOrEmpty(currentWindow) &&
                    currentWindow != _lastWindowName &&
                    !currentWindow.Contains("Flux"))
                {
                    // Собираем МЕГА-КОНТЕКСТ
                    StringBuilder fullContext = new StringBuilder();
                    fullContext.AppendLine($"[EVENT] Focus switched to: {currentWindow}");
                    fullContext.AppendLine("[UI TREE]");
                    fullContext.AppendLine(_cortex.GetLayer3_UIHierarchy());

                    // Записываем в базу
                    await _memory.Save(fullContext.ToString(), currentWindow);

                    _lastWindowName = currentWindow;
                }
            }
            catch { /* Игнорируем ошибки фона */ }
        }
    }
}
