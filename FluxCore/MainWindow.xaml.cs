using System;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FluxCore
{
    // --- МОДЕЛЬ СООБЩЕНИЯ ---
    public class ChatMessage
    {
        public string Text { get; set; } = string.Empty;
        public bool IsUser { get; set; }
    }

    // --- ГЛАВНОЕ ОКНО ---
    public partial class MainWindow : Window
    {
        // СЕРВИСЫ (Если каких-то файлов нет, просто закомментируй строки с ошибками)
        private SensoryCortex? _cortex;
        private NeuralLink? _neuralLink;
        private OmniLoop? _omniLoop;
        private AudioService? _audio;

        private DispatcherTimer? _heartbeat;
        private bool _isProcessing = false;

        public ObservableCollection<ChatMessage> Messages { get; set; } = new ObservableCollection<ChatMessage>();

        public MainWindow()
        {
            InitializeComponent();

            // 1. Привязываем чат
            if (ChatList != null) ChatList.ItemsSource = Messages;

            // 2. Включаем неоновый блюр при загрузке
            this.Loaded += (s, e) =>
            {
                NeonBlur.Enable(this); // Включаем размытие фона
                InitializeSystem();    // Запускаем ИИ
            };
        }

        private void InitializeSystem()
        {
            try
            {
                AddMessage("FluxCore Systems: ONLINE", false);

                string key = "ТВОЙ_КЛЮЧ_API";

                // Инициализация мозгов
                // Если нет этих файлов, закомментируй этот блок
                _cortex = new SensoryCortex();
                _neuralLink = new NeuralLink(key);
                _omniLoop = new OmniLoop(_cortex, _neuralLink);

                _audio = new AudioService();
                _audio.OnFinalText += async (txt) => await ProcessRequest(txt);
                _audio.StartContinuousRecording();

                // Таймер "сердцебиения"
                _heartbeat = new DispatcherTimer();
                _heartbeat.Interval = TimeSpan.FromSeconds(2);
                _heartbeat.Tick += (s, e) => { if (_omniLoop != null) _omniLoop.Pulse(); };
                _heartbeat.Start();
            }
            catch (Exception ex)
            {
                AddMessage($"System Warning: {ex.Message}", false);
            }
        }

        private async Task ProcessRequest(string text)
        {
            if (_isProcessing || text.Length < 2) return;
            _isProcessing = true;

            Dispatcher.Invoke(() => AddMessage(text, true));
            Dispatcher.Invoke(() => AddMessage("Computing...", false));

            try
            {
                // Запрос к ИИ
                string response = await _omniLoop!.UserQuery(text);

                Dispatcher.Invoke(() => {
                    if (Messages.Count > 0 && Messages[Messages.Count - 1].Text == "Computing...")
                        Messages.RemoveAt(Messages.Count - 1);

                    AddMessage(response, false);
                });
            }
            catch
            {
                // Игнорируем ошибки сети
            }

            _isProcessing = false;
        }

        private void AddMessage(string text, bool isUser)
        {
            Messages.Add(new ChatMessage { Text = text, IsUser = isUser });
            if (ChatList != null && ChatList.Items.Count > 0)
                ChatList.ScrollIntoView(ChatList.Items[ChatList.Items.Count - 1]);
        }

        // ВАЖНО: Метод перетаскивания (упомянут в твоем XAML)
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) this.DragMove();
        }
    }

    // --- УНИКАЛЬНЫЙ КЛАСС БЛЮРА (Я переименовал его, чтобы не было ошибок дубликатов) ---
    public static class NeonBlur
    {
        [DllImport("user32.dll")]
        internal static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        [StructLayout(LayoutKind.Sequential)]
        internal struct WindowCompositionAttributeData
        {
            public WindowCompositionAttribute Attribute;
            public IntPtr Data;
            public int SizeData;
        }

        internal enum WindowCompositionAttribute { WCA_ACCENT_POLICY = 19 }
        internal enum AccentState
        {
            ACCENT_DISABLED = 0,
            ACCENT_ENABLE_BLURBEHIND = 3, // Классический блюр
            ACCENT_ENABLE_ACRYLICBLURBEHIND = 4
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct AccentPolicy
        {
            public AccentState AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        public static void Enable(Window window)
        {
            var windowHelper = new WindowInteropHelper(window);
            var accent = new AccentPolicy();

            // Используем режим 3 (BLURBEHIND), он лучше всего сочетается с твоим цветом #CC101010
            accent.AccentState = AccentState.ACCENT_ENABLE_BLURBEHIND;
            accent.GradientColor = 0;

            var accentStructSize = Marshal.SizeOf(accent);
            var accentPtr = Marshal.AllocHGlobal(accentStructSize);
            Marshal.StructureToPtr(accent, accentPtr, false);

            var data = new WindowCompositionAttributeData();
            data.Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY;
            data.SizeData = accentStructSize;
            data.Data = accentPtr;

            SetWindowCompositionAttribute(windowHelper.Handle, ref data);
            Marshal.FreeHGlobal(accentPtr);
        }
    }
}