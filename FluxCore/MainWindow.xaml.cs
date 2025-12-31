using Application = System.Windows.Application;       // <--- ВОТ ЭТО ИСПРАВИТ ОШИБКУ
using Brushes = System.Windows.Media.Brushes;         // Исправляет Brushes
using Color = System.Windows.Media.Color;             // Исправляет Color
using KeyEventArgs = System.Windows.Input.KeyEventArgs; // Исправляет KeyEventArgs
using Point = System.Windows.Point;         // Используем WPF Point
using Clipboard = System.Windows.Clipboard;

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
using System.ComponentModel;

namespace FluxCore
{
    public class ChatMessage : INotifyPropertyChanged
    {
        private string _text = "";

        public string Text
        {
            get => _text;
            set
            {
                _text = value;
                OnPropertyChanged("Text");
            }
        }

        public bool IsUser { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class AppConfig
    {
        public double R { get; set; } = 16;
        public double G { get; set; } = 16;
        public double B { get; set; } = 18;
        public double Alpha { get; set; } = 230;
        public string Name { get; set; } = "Flux ai";
        public bool RequireWakeWord { get; set; } = false;
    }

    public partial class MainWindow : Window
    {
        // --- МОЗГИ (Они определены в CoreArchitecture.cs или ниже) ---
        private SensoryCortex? _cortex;
        private NeuralLink? _neuralLink;
        private OmniLoop? _omniLoop;
        private AudioService? _audioService;
        private MemoryService? _memory;
        private System.Windows.Threading.DispatcherTimer _bgTimer;
        private string _lastWindowName = "";
        private DateTime _appStartTime = DateTime.Now;

        private bool _isProcessing = false;
        private bool _isSecondary = false;
        private bool _isMicOn = false;
        private bool _requireWakeWord = false;
        private string _panelName = "Flux ai";

        private StringBuilder _voiceLog = new StringBuilder();
        public ObservableCollection<ChatMessage> Messages { get; set; } = new ObservableCollection<ChatMessage>();


        private const string SessionFile = "session_history.json";

        // WINAPI
        private const int HOTKEY_ID = 9000;
        private const uint MOD_ALT = 0x0001;
        private const uint VK_F = 0x46;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);

        private const int VK_LCONTROL = 0xA2; // Код левого контрола
        private System.Windows.Threading.DispatcherTimer _inputTimer; // Таймер для клавиатуры

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

        private void LoadSession()
        {
            try
            {
                if (File.Exists(SessionFile))
                {
                    var json = File.ReadAllText(SessionFile);
                    var loadedMsgs = JsonSerializer.Deserialize<List<ChatMessage>>(json);
                    if (loadedMsgs != null)
                    {
                        Messages.Clear();
                        foreach (var msg in loadedMsgs) Messages.Add(msg);
                        AddMessage("[System] Previous session context restored.", false);
                        ScrollToBottom();
                    }
                }
            }
            catch { /* Ошибка чтения истории не должна ломать запуск */ }
        }
        private void SaveSession()
        {
            try
            {
                // Сохраняем последние 50 сообщений, чтобы файл не раздувался до гигабайтов, 
                // но контекста хватало надолго.
                var historyToSave = Messages.Skip(Math.Max(0, Messages.Count - 50)).ToList();
                var json = JsonSerializer.Serialize(historyToSave);
                File.WriteAllText(SessionFile, json);
            }
            catch { }
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
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


            LoadSession();
            SaveSession();
            InitializeServices();
            LoadConfig();
            ApplyColors();
            UpdateMicButtonVisuals();

            if (!_isSecondary) FadeIn();
        }

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
                // 1. Инициализация Мозгов
                // Если ты не создал файл CoreArchitecture.cs, убедись, что классы SensoryCortex и OmniLoop есть в проекте!
                _cortex = new SensoryCortex();
                _neuralLink = new NeuralLink("AIzaSyDcSz3EBGyUT1NRkMwDzNfEFQk_8KfWFQs");
                _omniLoop = new OmniLoop(_cortex, _neuralLink);

                // 2. Инициализация Аудио
                _audioService = new AudioService();

                _memory = new MemoryService();

                _inputTimer = new System.Windows.Threading.DispatcherTimer();
                _inputTimer.Tick += InputTimer_Tick;
                _inputTimer.Interval = TimeSpan.FromMilliseconds(20); // Проверяем 50 раз в секунду
                _inputTimer.Start();

                // ИСПРАВЛЕНИЕ AWAIT: Добавили async/await в лямбду
                _audioService.OnFinalText += async (text) => await ProcessRequest(text);

                _audioService.OnError += (err) => Dispatcher.Invoke(() => AddMessage($"Audio Error: {err}", false));

                if (_isMicOn) _audioService.StartContinuousRecording();
                if (!_isSecondary) AddMessage($"Flux Core Ready.", false);

                _bgTimer = new System.Windows.Threading.DispatcherTimer();
                _bgTimer.Tick += BackgroundMonitor_Tick;
                _bgTimer.Interval = TimeSpan.FromMilliseconds(500);
                _bgTimer.Start();
            }
            catch (Exception ex) { AddMessage($"Init Failed: {ex.Message}", false); }
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

                    // Добавляем UI (структуру кнопок)
                    fullContext.AppendLine("[UI TREE]");
                    fullContext.AppendLine(_cortex.GetLayer3_UIHierarchy());

                    // Добавляем Текст (OCR)
                    fullContext.AppendLine("[VISUAL TEXT]");
                    // Тут await важен, так как OCR требует времени
                    fullContext.AppendLine(await _cortex.GetVisualContext());

                    // Добавляем Процессы
                    fullContext.AppendLine("[SYSTEM STATE]");
                    fullContext.AppendLine(_cortex.GetRunningProcesses());
                    fullContext.AppendLine(_cortex.GetSystemInfo());

                    // Записываем в базу
                    await _memory.Save(fullContext.ToString(), currentWindow);

                    _lastWindowName = currentWindow;

                    // Для отладки (можно убрать)
                    // System.Diagnostics.Debug.WriteLine($"[SNAPSHOT SAVED] {currentWindow}");
                }
            }
            catch { /* Игнорируем ошибки фона */ }
        }

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
        private void InputTimer_Tick(object? sender, EventArgs e)
        {
            // Проверяем физическое состояние Левого Ctrl
            // (GetAsyncKeyState работает везде, даже если окно свернуто)
            bool isCtrlDown = (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0;

            if (isCtrlDown)
            {
                // Если нажали и микрофон еще не включен
                if (!_isMicOn && !_isProcessing)
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

            Dispatcher.Invoke(() => {
                // Проверка WakeWord... (твой код)
            });

            if (_isProcessing) return;
            _isProcessing = true;

            // 1. Добавляем сообщение пользователя в UI и в ИСТОРИЮ
            Dispatcher.Invoke(() => AddMessage(userVoice, true));

            try
            {
                // --- СБОР СЫРЫХ ДАННЫХ ---
                string app = _cortex?.GetActiveWindow() ?? "Unknown";
                // Получаем полный UI с экрана (сырой)
                string ui = _cortex?.GetLayer3_UIHierarchy() ?? "NO UI DATA";
                // Получаем OCR (текст с экрана)
                string ocr = _cortex != null ? await _cortex.GetVisualContext() : "NO VISUAL DATA";

                // Объединяем сырые данные
                string fullScreenContext = $"WINDOW: {app}\n\n[UI TREE]\n{ui}\n\n[OCR TEXT]\n{ocr}";

                // --- ПАМЯТЬ ---
                List<string> memories = new List<string>();
                if (_memory != null)
                {
                    // Сохраняем текущий момент в долгосрочную память
                    await _memory.Save(ocr, app);
                    memories = await _memory.GetRecent(_appStartTime);
                }
                string memoryBlock = string.Join("\n", memories);

                // --- ПОДГОТОВКА ИСТОРИИ ---
                // Берем сообщения из UI (ObservableCollection)
                // Исключаем последнее (которое мы только что добавили - userVoice), 
                // так как метод ChatWithHistory сам добавит его с контекстом.
                var chatHistory = Messages.Where(m => !string.IsNullOrEmpty(m.Text)).ToList();
                // Удаляем последнее сообщение пользователя из списка "истории", 
                // потому что мы передадим его отдельно как "активный запрос" с прикрепленным контекстом.
                if (chatHistory.Count > 0 && chatHistory.Last().IsUser && chatHistory.Last().Text == userVoice)
                {
                    chatHistory.RemoveAt(chatHistory.Count - 1);
                }

                // --- ЗАПРОС К МОЗГУ ---
                // ВАЖНО: Мы используем обновленный GeminiService
                // Если у тебя _neuralLink или _omniLoop - адаптируй вызов там, или вызывай сервис напрямую
                // Предположим, мы вызываем сервис напрямую для наглядности:

                var gemini = new GeminiService("AIzaSyDcSz3EBGyUT1NRkMwDzNfEFQk_8KfWFQs");

                string answer = await gemini.ChatWithHistory(
                    chatHistory,    // Вся история чата
                    userVoice,      // Текущий вопрос
                    fullScreenContext, // Сырые данные экрана (обновленные!)
                    app,            // Имя окна
                    memoryBlock     // Долгосрочная память
                );

                // --- ОБРАБОТКА ОТВЕТА ---
                ExecuteCommands(answer);

                string cleanAnswer = answer
                    .Replace("[[OPEN:", "Launching: ")
                    .Replace("[[SEARCH:", "Searching: ")
                    .Replace("]]", "");

                // Вывод ответа (он автоматически попадет в Messages и сохранится при выходе)
                await Dispatcher.Invoke(async () => await StreamMessage(cleanAnswer));

                // Фоновое сохранение сессии после каждого ответа (на случай вылета)
                SaveSession();
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AddMessage($"Core Error: {ex.Message}", false));
            }
            finally
            {
                _isProcessing = false;
            }
        }

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
            catch { AddMessage("Could not save debug dump.", false); }
        }

        private async void Btn_DebugOCR_Click(object sender, RoutedEventArgs e)
        {
            string visual = _cortex != null ? await _cortex.GetVisualContext() : "Offline";
            string preview = visual.Length > 200 ? visual.Substring(0, 200) + "..." : visual;
            AddMessage($"[OCR VISION]:\n{preview}", false);
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
        private void NameBox_TextChanged(object sender, TextChangedEventArgs e) { _panelName = NameBox.Text; TitleText.Text = _panelName.ToUpper(); }

        private void WakeWord_Changed(object sender, RoutedEventArgs e) { _requireWakeWord = WakeWordCheck.IsChecked == true; }
        private void Btn_NewPanel_Click(object sender, RoutedEventArgs e) { var p = new MainWindow(true, "Flux Unit"); p.Left = this.Left + 40; p.Top = this.Top + 40; p.Show(); }
        private void Btn_Mic_Click(object sender, RoutedEventArgs e) => ToggleMic(!_isMicOn);
        private void ToggleMic(bool state)
        {
            _isMicOn = state;

            if (_isMicOn)
            {
                // Запуск (он void, тут всё ок)
                _audioService?.StartContinuousRecording();

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

        // Метод для добавления сообщения (обычный)
        private void AddMessage(string text, bool isUser)
        {
            Messages.Add(new ChatMessage { Text = text, IsUser = isUser });
            ScrollToBottom();
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

            StringBuilder sb = new StringBuilder();
            char[] chars = fullText.ToCharArray();

            // УСКОРЕНИЕ: Проходим циклом
            for (int i = 0; i < chars.Length; i++)
            {
                sb.Append(chars[i]);

                // ОБНОВЛЕНИЕ UI:
                // Чтобы не тормозило, обновляем UI не на каждой букве, а каждые 3-5 букв
                // Или если это последняя буква
                if (i % 3 == 0 || i == chars.Length - 1)
                {
                    botMsg.Text = sb.ToString();

                    // Автоскролл (чуть реже, чтобы не дергалось)
                    if (i % 10 == 0 && VisualTreeHelper.GetChildrenCount(ChatList) > 0)
                    {
                        var border = VisualTreeHelper.GetChild(ChatList, 0) as Border;
                        var scroller = VisualTreeHelper.GetChild(border, 0) as ScrollViewer;
                        scroller?.ScrollToBottom();
                    }

                    // МИНИМАЛЬНАЯ ЗАДЕРЖКА (1 мс)
                    // Это создает эффект "Хакерского потока"
                    await Task.Delay(1);
                }
            }
            // Финальный скролл
            botMsg.Text = sb.ToString();
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

        private void LoadConfig() { try { if (File.Exists("config.json")) { var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText("config.json")); if (cfg != null) { SliderR.Value = cfg.R; SliderG.Value = cfg.G; SliderB.Value = cfg.B; SliderAlpha.Value = cfg.Alpha; _panelName = cfg.Name ?? "Flux ai"; NameBox.Text = _panelName; TitleText.Text = _panelName.ToUpper(); _requireWakeWord = cfg.RequireWakeWord; WakeWordCheck.IsChecked = _requireWakeWord; } } } catch { } }
        private void SaveConfig() { if (!_isSecondary) try { File.WriteAllText("config.json", JsonSerializer.Serialize(new AppConfig { R = SliderR.Value, G = SliderG.Value, B = SliderB.Value, Alpha = SliderAlpha.Value, Name = _panelName, RequireWakeWord = _requireWakeWord })); } catch { } }
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e) { if (!_isSecondary) { UnregisterHotKey(new WindowInteropHelper(this).Handle, HOTKEY_ID); SaveConfig(); } _audioService?.Dispose(); }
    }
}