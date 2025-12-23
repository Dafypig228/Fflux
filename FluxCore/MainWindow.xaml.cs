using System;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace FluxCore
{
    public class ChatMessage { public string Text { get; set; } = ""; public bool IsUser { get; set; } }

    public partial class MainWindow : Window
    {
        // СЕРВИСЫ
        private AudioService? _audioService;
        private ScreenService? _screenService;
        private ContextService? _contextService; // <--- НОВЫЙ СЕРВИС
        private GeminiService? _geminiService;

        private bool _isProcessing = false;
        public ObservableCollection<ChatMessage> Messages { get; set; } = new ObservableCollection<ChatMessage>();

        public MainWindow()
        {
            InitializeComponent();
            ChatList.ItemsSource = Messages;
            AddButton("🎤 Test", () => ProcessRequest("Что это за кнопка?")); // Кнопка для теста без микрофона
            AddButton("❌ Exit", () => Application.Current.Shutdown());
            InitializeServices();
        }

        private void InitializeServices()
        {
            try
            {
                _geminiService = new GeminiService("AIzaSyDcSz3EBGyUT1NRkMwDzNfEFQk_8KfWFQs"); // Твой ключ
                _screenService = new ScreenService();
                _contextService = new ContextService(); // Инициализация контекста

                _audioService = new AudioService();
                _audioService.OnFinalText += async (text) => await ProcessRequest(text);
                _audioService.StartContinuousRecording();

                AddMessage("👁️ FluxCore Context-Aware: ONLINE", false);
            }
            catch (Exception ex) { AddMessage($"Ошибка старта: {ex.Message}", false); }
        }

        // --- ГЛАВНАЯ ЛОГИКА (PIPELINE) ---
        // Вставь это в метод ProcessRequest
        private async Task ProcessRequest(string userVoice)
        {
            if (_isProcessing || string.IsNullOrWhiteSpace(userVoice) || userVoice.Length < 2) return;
            _isProcessing = true;

            Dispatcher.Invoke(() => AddMessage(userVoice, true));

            // Индикатор работы (чтобы ты видел, что процесс идет)
            Dispatcher.Invoke(() => AddMessage("⚡ Анализ Tier 1...", false));

            try
            {
                // 1. Метаданные (Мгновенно)
                string appMeta = _contextService!.GetAppMetadata();

                // 2. Что под мышкой (Мгновенно)
                string mouseData = _contextService.GetElementUnderMouse();

                // 3. Текст экрана (OCR)
                // Если экран не менялся - можно было бы кэшировать, но пока берем свежий
                string screenText = await _screenService!.AnalyzeScreenAsync();

                // 4. Отправка в Gemini
                string answer = await _geminiService!.AskContextAware(userVoice, appMeta, mouseData, screenText);

                Dispatcher.Invoke(() => {
                    // Удаляем "Анализ..."
                    if (Messages.Count > 0 && Messages[Messages.Count - 1].Text.StartsWith("⚡"))
                        Messages.RemoveAt(Messages.Count - 1);

                    AddMessage(answer, false);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AddMessage($"Ошибка: {ex.Message}", false));
            }

            _isProcessing = false;
        }

        // --- Стандартные методы UI (Кнопки, Скролл, Drag) ---
        private void AddMessage(string text, bool isUser)
        {
            Messages.Add(new ChatMessage { Text = text, IsUser = isUser });
            if (ChatList.Items.Count > 0) ChatList.ScrollIntoView(ChatList.Items[ChatList.Items.Count - 1]);
        }
        private void AddButton(string title, Action onClick)
        {
            var btn = new Button { Content = title, Height = 30, Margin = new Thickness(5), Background = Brushes.Transparent, Foreground = Brushes.White };
            btn.Click += (s, e) => onClick();
            ButtonContainer.Children.Add(btn);
        }
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
    }
}