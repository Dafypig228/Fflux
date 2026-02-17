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
        // --- МОЗГИ ---
        private SensoryCortex? _cortex;
        // private NeuralLink? _neuralLink; // Удаляем старый линк
        // private OmniLoop? _omniLoop;     // Удаляем старый цикл
        private AudioService? _audioService;
        private MemoryService? _memory;
        private Hippocampus? _hippocampus; // NEW: Reflexion Memory
        private GeminiService? _gemini;   // Единый сервис
        private ExecutionAgent? _executor; // Execution Agent
        private OrchestratorAgent? _orchestrator; // Orchestrator Agent
        private WindowsAutomationAgent? _automation; // Windows Automation
        private CodeExecutionAgent? _codeRunner; // Code Execution Sandbox
        private ValidatorAgent? _validator; // NEW: Visual Validation
        private JarvisCore? _jarvis; // NEW: JARVIS Intelligence Core
        private ReflectionAgent? _reflection; // NEW: Error Recovery
        private System.Windows.Threading.DispatcherTimer _bgTimer;
        private string _lastWindowName = "";
        private DateTime _appStartTime = DateTime.Now;

        private FluxBrain? _brain; // NEW: Central intelligence router
        private bool _isSecondary = false;
        private bool _isMicOn = false;
        private bool _requireWakeWord = false;
        private string _panelName = "Flux ai";
        
        // Permission System
        private TaskCompletionSource<bool>? _permissionResult;
        private string _pendingAction = "";
        private string _pendingTarget = "";

        private StringBuilder _voiceLog = new StringBuilder();
        public ObservableCollection<ChatMessage> Messages { get; set; } = new ObservableCollection<ChatMessage>();


        private const string SessionFile = "session_history.json";
        private const string API_KEY = "AIzaSyDcSz3EBGyUT1NRkMwDzNfEFQk_8KfWFQs"; // TODO: Move to config

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
                // Сохраняем последние 50 сообщений
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
                _cortex = new SensoryCortex();
                _gemini = new GeminiService(API_KEY);
                
                // Внедряем Gemini в память
                _memory = new MemoryService(_gemini);
                
                // NEW: Инициализация Оркестратора
                _orchestrator = new OrchestratorAgent(_gemini);
                
                // NEW: Инициализация Windows Automation
                _automation = new WindowsAutomationAgent();
                _hippocampus = new Hippocampus();

                // 2. Инициализация Аудио
                _audioService = new AudioService();

                _inputTimer = new System.Windows.Threading.DispatcherTimer();
                _inputTimer.Tick += InputTimer_Tick;
                _inputTimer.Interval = TimeSpan.FromMilliseconds(20); 
                _inputTimer.Start();

                _audioService.OnFinalText += async (text) => await ProcessRequest(text);
                _audioService.OnError += (err) => Dispatcher.Invoke(() => AddMessage($"Audio Error: {err}", false));

                if (_isMicOn) _audioService.StartContinuousRecording();
                if (!_isSecondary) AddMessage($"Flux Core Ready (Vector Memory Online).", false);

                // NEW: Initialize JARVIS Core
                _codeRunner = new CodeExecutionAgent();
                _reflection = new ReflectionAgent(_gemini);
                _validator = new ValidatorAgent(_gemini);
                _jarvis = new JarvisCore(
                    _gemini,
                    _executor ?? new ExecutionAgent(),
                    _automation,
                    _codeRunner,
                    _cortex,  // NEW: Pass cortex for element detection
                    _hippocampus!,
                    _validator!,
                    () => _cortex?.GetScreenBase64() ?? "",
                    () => _cortex?.GetActiveWindow() ?? "Unknown",
                    (msg) => Dispatcher.InvokeAsync(() => AddMessage(msg, false))
                );

                // --- NEURO-HUD WIRING ---
                _jarvis.OnStateChanged += (state) => Dispatcher.InvokeAsync(() => UpdateHudState(state));
                _jarvis.OnThought += (thought) => Dispatcher.InvokeAsync(() => UpdateHudContent(thought));
                _jarvis.OnAction += (action) => Dispatcher.InvokeAsync(() => UpdateHudAction(action));
                _jarvis.OnValidation += (success, reason) => Dispatcher.InvokeAsync(() => UpdateHudValidation(success, reason));

                // --- CHAT RESPONSE WIRING ---
                // This shows the AI's actual response in the chat (not just thoughts/actions)
                _jarvis.OnResponse += (response) => Dispatcher.InvokeAsync(() =>
                {
                    AddMessage(response, false);  // false = AI message
                    NeuroHudPanel.Visibility = Visibility.Collapsed;  // Hide HUD after response
                    StatusText.Visibility = Visibility.Visible;
                    StatusText.Text = "Ready";
                    ScrollToBottom();
                });

                // --- SMART MODE: Screen Access Callback ---
                _jarvis.SetScreenAccessCallback(RequestScreenAccessAsync);

                // ============================================
                // FLUXBRAIN: Central Intelligence Router
                // ============================================
                _brain = new FluxBrain(_gemini, _jarvis, _memory, _hippocampus, _cortex);

                // Wire FluxBrain events to UI
                _brain.OnMessage += (text, isUser) => Dispatcher.InvokeAsync(async () =>
                {
                    if (isUser)
                        AddMessage(text, true);
                    else
                        await StreamMessage(text);
                    ScrollToBottom();
                    SaveSession();
                });

                _brain.OnStatusChanged += (status) => Dispatcher.InvokeAsync(() =>
                {
                    StatusText.Text = status;
                });

                _brain.Start();
                System.Diagnostics.Debug.WriteLine("[BRAIN] FluxBrain started successfully");

                _bgTimer = new System.Windows.Threading.DispatcherTimer();
                _bgTimer.Tick += BackgroundMonitor_Tick;
                _bgTimer.Interval = TimeSpan.FromMilliseconds(500);
                _bgTimer.Start();
            }
            catch (Exception ex) { AddMessage($"Init Failed: {ex.Message}", false); }
        }

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
                    
                    // !!! ВАЖНО: Тут мы НЕ делаем тяжелый OCR каждый раз, чтобы не тормозить.
                    // fullContext.AppendLine(await _cortex.GetVisualContext());

                    // Записываем в базу
                    await _memory.Save(fullContext.ToString(), currentWindow);

                    _lastWindowName = currentWindow;
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

            // FluxBrain handles everything: classification, routing, parallel execution
            // It never blocks, never drops requests
            await _brain.SubmitAsync(userVoice);
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

        // =========================================
        // TAB NAVIGATION
        // =========================================
        private void Tab_Chat_Click(object sender, RoutedEventArgs e)
        {
            ChatList.Visibility = Visibility.Visible;
            MemoryPanel.Visibility = Visibility.Collapsed;
            UpdateTabColors("chat");
        }

        private async void Tab_Memory_Click(object sender, RoutedEventArgs e)
        {
            ChatList.Visibility = Visibility.Collapsed;
            MemoryPanel.Visibility = Visibility.Visible;
            UpdateTabColors("memory");
            
            // Load memories
            if (_memory != null)
            {
                var memories = await _memory.GetRecentMemoriesAsync(50);
                MemoryList.ItemsSource = memories;
            }
        }

        private void UpdateTabColors(string activeTab)
        {
            TabChat.Foreground = activeTab == "chat" ? new SolidColorBrush(Color.FromRgb(0, 255, 209)) : new SolidColorBrush(Color.FromRgb(170, 170, 170));
            TabMemory.Foreground = activeTab == "memory" ? new SolidColorBrush(Color.FromRgb(0, 255, 209)) : new SolidColorBrush(Color.FromRgb(170, 170, 170));
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

        // =========================================
        // SETTINGS HANDLERS
        // =========================================
        private AppSettings _settings = new AppSettings();
        
        private void SliderOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded) return;

            // OPACITY FIX: Change background alpha instead of window opacity
            // Window opacity doesn't work well with AllowsTransparency="True"
            byte alpha = (byte)(SliderOpacity.Value * 255);
            TintBrush.Color = Color.FromArgb(alpha, 16, 16, 21);

            _settings.WindowOpacity = SliderOpacity.Value;
            _settings.Save();
        }
        
        private void SliderBlur_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded) return;
            BackgroundBlur.Radius = SliderBlur.Value;
            _settings.BlurRadius = SliderBlur.Value;
            _settings.Save();
        }
        
        private void AutoMinimize_Changed(object sender, RoutedEventArgs e)
        {
            _settings.AutoMinimizeOnComplete = AutoMinimizeCheck.IsChecked == true;
            _settings.Save();
        }
        
        private void Btn_Minimize_Click(object sender, RoutedEventArgs e) => this.WindowState = WindowState.Minimized;
        
        private void Btn_ClearChat_Click(object sender, RoutedEventArgs e)
        {
            Messages.Clear();
            AddMessage("Chat cleared.", false);
        }
        
        private void ToggleSettings_Click(object sender, RoutedEventArgs e) => SettingsPanel.Visibility = (SettingsPanel.Visibility == Visibility.Visible) ? Visibility.Collapsed : Visibility.Visible;
        private void OnDrag(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) this.DragMove(); }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) { if (msg == 0x0312 && wParam.ToInt32() == HOTKEY_ID) { ToggleAllPanels(); handled = true; } return IntPtr.Zero; }
        private void ToggleAllPanels() { bool show = (this.Visibility != Visibility.Visible || this.Opacity < 0.1); foreach (Window w in Application.Current.Windows) { if (w is MainWindow mw) { if (show) { mw.Opacity = 0; mw.Visibility = Visibility.Visible; mw.FadeIn(); } else mw.FadeOut(); } } if (show) { this.Activate(); InputBox.Focus(); } }
        public void FadeIn() { BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))); }
        public void FadeOut() { var a = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200)); a.Completed += (s, e) => Visibility = Visibility.Hidden; BeginAnimation(OpacityProperty, a); }
        public void ForceHide() { Dispatcher.Invoke(() => FadeOut()); }

        private void LoadConfig()
        {
            try
            {
                _settings = AppSettings.Load();
                SliderOpacity.Value = _settings.WindowOpacity;
                SliderBlur.Value = _settings.BlurRadius;
                _panelName = _settings.WakeWord;
                NameBox.Text = _panelName;
                TitleText.Text = _panelName.ToUpper();
                _requireWakeWord = _settings.RequireWakeWord;
                WakeWordCheck.IsChecked = _requireWakeWord;
                AutoMinimizeCheck.IsChecked = _settings.AutoMinimizeOnComplete;
                
                // Apply settings
                this.Opacity = _settings.WindowOpacity;
                BackgroundBlur.Radius = _settings.BlurRadius;
            }
            catch { }
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
        
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e) { if (!_isSecondary) { UnregisterHotKey(new WindowInteropHelper(this).Handle, HOTKEY_ID); SaveConfig(); } _audioService?.Dispose(); _ = _executor?.DisposeAsync(); }

        // =========================================
        // PERMISSION DIALOG SYSTEM
        // =========================================

        // Screen Access Permission (for Smart Mode)
        private TaskCompletionSource<bool>? _screenAccessResult;

        private void Btn_PermissionAllow_Click(object sender, RoutedEventArgs e)
        {
            PermissionOverlay.Visibility = Visibility.Collapsed;
            _permissionResult?.TrySetResult(true);
            _screenAccessResult?.TrySetResult(true);
        }

        private void Btn_PermissionDeny_Click(object sender, RoutedEventArgs e)
        {
            PermissionOverlay.Visibility = Visibility.Collapsed;
            _permissionResult?.TrySetResult(false);
            _screenAccessResult?.TrySetResult(false);
        }

        /// <summary>
        /// Shows screen access permission dialog for Smart Mode.
        /// Called when AI needs to use screen-based commands.
        /// </summary>
        private async Task<bool> RequestScreenAccessAsync(string reason)
        {
            _screenAccessResult = new TaskCompletionSource<bool>();

            await Dispatcher.InvokeAsync(() =>
            {
                PermissionActionText.Text = "🖥️ Screen Access Required:";
                PermissionDetailsText.Text = $"{reason}\n\nAllow Flux to view and interact with your screen?";
                PermissionOverlay.Visibility = Visibility.Visible;

                // Make window visible if hidden
                if (this.Opacity < 0.5)
                    this.Opacity = 1;
            });

            return await _screenAccessResult.Task;
        }

        /// <summary>
        /// Shows permission dialog and waits for user response.
        /// </summary>
        private string _expectedTarget = ""; // Track what app we expect to be focused

        private async Task<bool> RequestPermissionAsync(string actionType, string target)
        {
            _pendingAction = actionType;
            _pendingTarget = target;
            _permissionResult = new TaskCompletionSource<bool>();

            Dispatcher.Invoke(() =>
            {
                PermissionActionText.Text = $"Flux wants to {actionType}:";
                PermissionDetailsText.Text = target;
                PermissionOverlay.Visibility = Visibility.Visible;
            });

            return await _permissionResult.Task;
        }

        /// <summary>
        /// Executes actions with permission. Handles MULTIPLE commands in sequence.
        /// </summary>
        private async Task<string> ExecuteWithPermissionAsync(string fullResponse, bool skipPermission = false)
        {
            if (_executor == null) _executor = new ExecutionAgent();
            if (_automation == null) _automation = new WindowsAutomationAgent();
            
            var results = new List<string>();
            
            // Extract all commands from response
            var commands = ExtractAllCommands(fullResponse);
            
            if (commands.Count == 0)
                return "";
            
            // Ask permission once for all commands (UNLESS skipped)
            if (!skipPermission)
            {
                string summary = string.Join(", ", commands.Take(5).Select(c => $"{c.Type}:{c.Arg}"));
                if (commands.Count > 5) summary += $" (+{commands.Count - 5} more)";
                
                if (!await RequestPermissionAsync($"execute {commands.Count} action(s)", summary))
                    return "[Permission denied]";
            }
            
            // Execute each command in sequence
            // Execute each command in sequence
            foreach (var originalCmd in commands)
            {
                // Map aliases to standard types
                var cmd = originalCmd;
                if (cmd.Type == "Typing") cmd = ("TYPE", cmd.Arg);
                if (cmd.Type == "Launching" || cmd.Type == "Opening") cmd = ("OPEN_APP", cmd.Arg);
                if (cmd.Type == "Clicking") cmd = ("CLICK", cmd.Arg);
                if (cmd.Type == "Writing") cmd = ("WRITE_FILE", cmd.Arg);
                if (cmd.Type == "Keys") cmd = ("KEYS", cmd.Arg);

                try
                {
                    string result = "";
                    
                    switch (cmd.Type)
                    {
                        case "OPEN_APP":
                            // Capture current window
                            string oldTitle = _cortex?.GetActiveWindow() ?? "";
                            
                            var appResult = await _executor.OpenApp(cmd.Arg);
                            result = appResult.Success ? appResult.Message : $"Error: {appResult.Message}";
                            
                            // Track what we expect to be focused
                            if (appResult.Success)
                            {
                                 _expectedTarget = cmd.Arg.ToLower();
                                 if (_expectedTarget.Contains("chrome")) _expectedTarget = "chrome";
                                 if (_expectedTarget.Contains("telegram")) _expectedTarget = "telegram";
                                 if (_expectedTarget.Contains("instagram")) _expectedTarget = "instagram"; ///chrome usually
                            }
                            
                            // Smart Wait: Poll until active window changes (max 8s)
                            // Optimization: If we just focused an existing app, the title might NOT change.
                            // So we check if the result message says "Focused existing".
                            bool justFocused = appResult.Message.Contains("Focused existing");
                            
                            if (appResult.Success)
                            {
                                int waited = 0;
                                int maxWait = justFocused ? 2000 : 8000; // Wait less if just focusing
                                
                                while (waited < maxWait)
                                {
                                    await Task.Delay(500);
                                    waited += 500;
                                    string newTitle = _cortex?.GetActiveWindow() ?? "";
                                    
                                    // If title changed OR we are just focusing and the title ALREADY contains the target
                                    if (newTitle != oldTitle && !string.IsNullOrEmpty(newTitle) && !newTitle.Contains("Flux"))
                                    {
                                        System.Diagnostics.Debug.WriteLine($"[FLUX] Window switched to: {newTitle}");
                                        break;
                                    }
                                    
                                    // If we focused existing, and the CURRENT title is already correct, stop waiting
                                    if (justFocused && !string.IsNullOrEmpty(newTitle) && newTitle.ToLower().Contains(_expectedTarget))
                                    {
                                         break;
                                    }
                                }
                            }

                            else
                            {
                                await Task.Delay(1000); // Wait a bit on error
                            }

                            // Reset automation target to find the new window
                            _automation = new WindowsAutomationAgent();
                            break;

                        case "LOG":
                            Dispatcher.Invoke(() => AddMessage($"[🧠] {cmd.Arg}", false));
                            result = "Logged thought.";
                            break;
                            
                        case "READ_FILE":
                            var readResult = await _executor.ReadFileAsync(cmd.Arg);
                            result = readResult.Success ? readResult.Message : $"Error: {readResult.Message}";
                            break;
                            
                        case "OPEN_URL":
                            var urlResult = await _executor.OpenUrlAsync(cmd.Arg);
                            result = urlResult.Success ? urlResult.Message : $"Error: {urlResult.Message}";
                            break;
                            
                        case "CLICK":
                            // STRICT SAFETY CHECK
                            if (!string.IsNullOrEmpty(_expectedTarget))
                            {
                                string current = _cortex?.GetActiveWindow()?.ToLower() ?? "";
                                string proc = _cortex?.GetActiveProcessName()?.ToLower() ?? "";

                                bool isMatch = current.Contains(_expectedTarget) || 
                                               proc.Contains(_expectedTarget) ||
                                               (_expectedTarget == "chrome" && (proc == "chrome" || current.Contains("google") || current.Contains("new tab") || current.Contains("start page") || current.Contains("search"))) ||
                                               (_expectedTarget == "instagram" && (proc == "chrome" || current.Contains("chrome")));
                                
                                if (!isMatch && !string.IsNullOrEmpty(current) && !current.Contains("flux"))
                                {
                                    result = $"[SAFETY STOP] Wrong Window! Expected: '{_expectedTarget}'. Actual: '{current}'.";
                                    Dispatcher.Invoke(() => AddMessage(result, false)); // Force log immediately
                                    results.Add(result); // Add to final result
                                    goto StopExecution; 
                                }
                            }
                            var clickResult = await _automation.ClickElementAsync(cmd.Arg);
                            result = clickResult.Success ? clickResult.Message : $"Error: {clickResult.Message}";
                            break;
                            
                        case "TYPE":
                            // STRICT SAFETY CHECK
                            if (!string.IsNullOrEmpty(_expectedTarget))
                            {
                                string current = _cortex?.GetActiveWindow()?.ToLower() ?? "";
                                string proc = _cortex?.GetActiveProcessName()?.ToLower() ?? "";

                                bool isMatch = current.Contains(_expectedTarget) || 
                                               proc.Contains(_expectedTarget) ||
                                               (_expectedTarget == "chrome" && (proc == "chrome" || current.Contains("google") || current.Contains("new tab") || current.Contains("start page") || current.Contains("search"))) ||
                                               (_expectedTarget == "instagram" && (proc == "chrome" || current.Contains("chrome")));
                                
                                if (!isMatch && !string.IsNullOrEmpty(current) && !current.Contains("flux"))
                                {
                                    result = $"[SAFETY STOP] Wrong Window! Expected: '{_expectedTarget}'. Actual: '{current}'.";
                                    Dispatcher.Invoke(() => AddMessage(result, false)); // Force log immediately
                                    results.Add(result); // Add to final result
                                    // Stop executing subsequent commands
                                    goto StopExecution; 
                                }
                            }

                            var typeResult = await _automation.TypeTextAsync("", cmd.Arg);
                            result = typeResult.Success ? typeResult.Message : $"Error: {typeResult.Message}";
                            await Task.Delay(300); // Wait for UI to process typed text
                            break;
                            
                        case "KEYS":
                            var keysResult = await _automation.SendKeysAsync(cmd.Arg);
                            result = keysResult.Success ? keysResult.Message : $"Error: {keysResult.Message}";
                            
                            // Smart delays based on what key was pressed
                            if (cmd.Arg.ToUpper().Contains("WIN"))
                            {
                                await Task.Delay(800); // Wait for Start Menu
                            }
                            else if (cmd.Arg.ToUpper() == "ENTER")
                            {
                                // ENTER after typing = app launch - wait for it to open!
                                await Task.Delay(2000); // Wait 2 seconds for app to fully open
                                _automation = new WindowsAutomationAgent(); // Reset to target new window
                            }
                            else
                            {
                                await Task.Delay(200);
                            }
                            break;
                            
                        // === CODE EXECUTION COMMANDS ===
                        case "WRITE_FILE":
                            if (_codeRunner == null) _codeRunner = new CodeExecutionAgent();
                            // Format: [[WRITE_FILE:path|content]]
                            var writeParts = cmd.Arg.Split(new[] { '|' }, 2);
                            if (writeParts.Length < 2)
                            {
                                result = "Error: WRITE_FILE format is [[WRITE_FILE:path|content]]";
                            }
                            else
                            {
                                var writeResult = await _codeRunner.WriteFileAsync(writeParts[0].Trim(), writeParts[1]);
                                result = writeResult.Success ? writeResult.Message : $"Error: {writeResult.Message}";
                            }
                            break;
                            
                        case "RUN_PYTHON":
                            if (_codeRunner == null) _codeRunner = new CodeExecutionAgent();
                            var pyResult = await _codeRunner.RunPythonAsync(cmd.Arg);
                            result = pyResult.Success ? $"Python output:\n{pyResult.Message}" : $"Error: {pyResult.Message}";
                            break;
                            
                        case "RUN_SHELL":
                            if (_codeRunner == null) _codeRunner = new CodeExecutionAgent();
                            var shellResult = await _codeRunner.RunPowerShellAsync(cmd.Arg);
                            result = shellResult.Success ? $"Shell output:\n{shellResult.Message}" : $"Error: {shellResult.Message}";
                            break;
                            
                        case "RUN_NODE":
                            if (_codeRunner == null) _codeRunner = new CodeExecutionAgent();
                            var nodeResult = await _codeRunner.RunNodeAsync(cmd.Arg);
                            result = nodeResult.Success ? $"Node.js output:\n{nodeResult.Message}" : $"Error: {nodeResult.Message}";
                            break;
                            
                        case "CLIPBOARD":
                            if (_codeRunner == null) _codeRunner = new CodeExecutionAgent();
                            var clipResult = _codeRunner.SetClipboard(cmd.Arg);
                            result = clipResult.Success ? clipResult.Message : $"Error: {clipResult.Message}";
                            break;
                            
                        case "DOWNLOAD_FILE":
                            if (_codeRunner == null) _codeRunner = new CodeExecutionAgent();
                            var dlParts = cmd.Arg.Split(new[] { '|' }, 2);
                            if (dlParts.Length < 2)
                                result = "Error: Format is [[DOWNLOAD_FILE:url|path]]";
                            else
                            {
                                var dlResult = await _codeRunner.DownloadFileAsync(dlParts[0].Trim(), dlParts[1].Trim());
                                result = dlResult.Success ? dlResult.Message : $"Error: {dlResult.Message}";
                            }
                            break;
                            
                        case "SCROLL":
                            var scrollResult = await _automation.ScrollAsync(cmd.Arg);
                            result = scrollResult.Success ? scrollResult.Message : $"Error: {scrollResult.Message}";
                            break;
                            
                        case "WINDOW":
                            var winResult = await _automation.WindowControlAsync(cmd.Arg);
                            result = winResult.Success ? winResult.Message : $"Error: {winResult.Message}";
                            break;
                    }
                    
                    // === VALIDATOR: Check if action was successful ===
                    if (!string.IsNullOrEmpty(result))
                    {
                        if (_validator == null && _gemini != null) 
                            _validator = new ValidatorAgent(_gemini);
                        
                        if (_validator != null)
                        {
                            var validation = await _validator.ValidateAsync(cmd.Arg, cmd.Type, result);
                            
                            if (!validation.Success)
                            {
                                result += $" [VALIDATOR: {validation.Message}]";
                                
                                // If should retry and we haven't retried yet, mark for attention
                                if (validation.ShouldRetry)
                                {
                                    result += " [RETRY RECOMMENDED]";
                                }
                            }
                            else
                            {
                                result += $" ✓";
                            }
                        }
                        
                        results.Add(result);
                    }
                }
                catch (Exception ex)
                {
                    results.Add($"Error in {cmd.Type}: {ex.Message}");
                }
            }
            
            StopExecution:
            return string.Join("\n", results);
        }
        
        /// <summary>
        /// Extracts all [[COMMAND:arg]] from text in order of appearance.
        /// </summary>
        private List<(string Type, string Arg)> ExtractAllCommands(string text)
        {
            var commands = new List<(int Position, string Type, string Arg)>();
            var commandTypes = new[] { 
                "OPEN_APP", "READ_FILE", "OPEN_URL", "CLICK", "TYPE", "KEYS", 
                "WRITE_FILE", "RUN_PYTHON", "RUN_SHELL", "RUN_NODE",
                "CLIPBOARD", "DOWNLOAD_FILE", "SCROLL", "WINDOW", "LOG",
                "Typing", "Launching", "Opening", "Clicking", "Writing", "Keys"
            };
            
            // Multiline commands that can contain newlines
            var multilineCommands = new HashSet<string> { "WRITE_FILE", "RUN_PYTHON", "RUN_SHELL", "RUN_NODE" };
            
            // Find ALL commands with their positions
            foreach (var cmdType in commandTypes)
            {
                string pattern = $"[[{cmdType}:";
                int searchStart = 0;
                
                while (true)
                {
                    int start = text.IndexOf(pattern, searchStart);
                    if (start < 0) break;
                    
                    int argStart = start + pattern.Length;
                    int end = text.IndexOf("]]", argStart);
                    
                    string arg;
                    if (end > argStart)
                    {
                        arg = text.Substring(argStart, end - argStart);
                        searchStart = end + 2;
                    }
                    else
                    {
                        // No closing ]] - for multiline, take until next [[ or end
                        // For single-line, take until newline
                        if (multilineCommands.Contains(cmdType))
                        {
                            // Find next command or end
                            int nextCmd = -1;
                            foreach (var type in commandTypes)
                            {
                                int pos = text.IndexOf($"[[{type}:", argStart);
                                if (pos > argStart && (nextCmd < 0 || pos < nextCmd))
                                    nextCmd = pos;
                            }
                            
                            if (nextCmd > argStart)
                                arg = text.Substring(argStart, nextCmd - argStart).TrimEnd();
                            else
                                arg = text.Substring(argStart).TrimEnd();
                        }
                        else
                        {
                            int newline = text.IndexOf('\n', argStart);
                            arg = newline > argStart ? text.Substring(argStart, newline - argStart) : text.Substring(argStart);
                        }
                        searchStart = argStart + arg.Length;
                    }
                    
                    // Store position for sorting
                    commands.Add((start, cmdType, arg.Trim()));
                }
            }
            
            // Sort by position in original text
            return commands.OrderBy(c => c.Position).Select(c => (c.Type, c.Arg)).ToList();
        }

        private string ExtractCommandArg(string text, string commandName)
        {
            string pattern = $"[[{commandName}:";
            int start = text.IndexOf(pattern);
            if (start < 0) return "";
            start += pattern.Length;
            
            // Try to find closing ]]
            int end = text.IndexOf("]]", start);
            
            // If no closing brackets, take until newline or end of string
            if (end < 0)
            {
                int newline = text.IndexOf('\n', start);
                end = newline > 0 ? newline : text.Length;
            }
            
            return text.Substring(start, end - start).Trim();
        }
    }
}