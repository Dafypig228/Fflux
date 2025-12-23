using FluxCore;
using System;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FluxCore
{
    public partial class MainWindow : Window
    {
        private AudioService _audioService; // Твой класс AudioService
        private bool _isVisible = false;
        private const int HOTKEY_ID = 9000;

        // Коллекция сообщений
        public ObservableCollection<ChatMessage> Messages { get; set; }
            = new ObservableCollection<ChatMessage>();

        public MainWindow()
        {
            InitializeComponent();

            // Связываем данные
            ChatList.ItemsSource = Messages;

            // Запускаем AudioService
            _audioService = new AudioService();
            _audioService.OnFinalText += text =>
            {
                Dispatcher.Invoke(() => AddMessage(text, true)); // Это ЮЗЕР
                // Тут потом подключишь ответ от ИИ:
                // Dispatcher.Invoke(() => AddMessage("Думаю...", false));
            };

            // Если была ошибка аудио - пишем её в чат
            _audioService.OnError += err =>
            {
                Dispatcher.Invoke(() => AddMessage($"⚠️ {err}", false));
            };

            _audioService.StartContinuousRecording();

            // Создаем кнопки
            AddButton("GPT-4", () => AddMessage("Режим GPT включен", false));
            AddButton("Очистить", () => Messages.Clear());
            AddButton("Выход", () => Application.Current.Shutdown());

            // Прячем окно при старте
            //this.Visibility = Visibility.Hidden;
        }

        private void AddMessage(string text, bool isUser)
        {
            Messages.Add(new ChatMessage { Text = text, IsUser = isUser });
            ChatScroller.ScrollToBottom();
        }

        // --- ДИЗАЙН КНОПОК ---
        private void AddButton(string title, Action onClick)
        {
            var btn = new Button
            {
                Content = title,
                Style = (Style)FindResource("ModernBtn") // Применяем наш крутой стиль
            };

            btn.Click += (s, e) => onClick();
            ButtonContainer.Children.Add(btn); // Используем Children, так как это StackPanel
        }

        // Перетаскивание окна
        private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            this.DragMove();
        }

        // --- ХОТКЕИ (Alt+Space) ---
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var handle = new WindowInteropHelper(this).Handle;

            RegisterHotKey(handle, HOTKEY_ID, 0x0001, 0x20);
            HwndSource.FromHwnd(handle).AddHook(HwndHook);

            // Убираем из Alt+Tab (ToolWindow)
            int exStyle = (int)GetWindowLong(handle, -20);
            exStyle |= 0x00000080;
            SetWindowLong(handle, -20, (IntPtr)exStyle);

            // Центрируем окно
            double screenW = SystemParameters.PrimaryScreenWidth;
            this.Left = (screenW - this.Width) / 2;
            this.Top = 80;
        }

        private void TogglePanel()
        {
            if (_isVisible)
            {
                // Анимация исчезновения
                var animY = new DoubleAnimation(0, -20, TimeSpan.FromMilliseconds(200));
                var animOpacity = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
                animOpacity.Completed += (s, e) => { this.Hide(); _isVisible = false; };

                PanelTransform.BeginAnimation(TranslateTransform.YProperty, animY);
                MainBorder.BeginAnimation(UIElement.OpacityProperty, animOpacity);
            }
            else
            {
                this.Show();
                this.Activate();
                _isVisible = true;

                // Анимация появления "Капли"
                var animY = new DoubleAnimation(-20, 0, TimeSpan.FromMilliseconds(400))
                {
                    EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 8 }
                };
                var animOpacity = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300));

                PanelTransform.BeginAnimation(TranslateTransform.YProperty, animY);
                MainBorder.BeginAnimation(UIElement.OpacityProperty, animOpacity);

                ChatScroller.ScrollToBottom();
            }
        }

        // Win32 API
        [DllImport("user32.dll")] private static extern IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll")] private static extern IntPtr GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0312 && wParam.ToInt32() == HOTKEY_ID) { TogglePanel(); handled = true; }
            return IntPtr.Zero;
        }
    }
}