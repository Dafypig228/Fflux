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
        private WindowsAutomationAgent? _automation; // Windows Automation
        private CodeExecutionAgent? _codeRunner; // Code Execution Sandbox
        private ValidatorAgent? _validator; // NEW: Visual Validation
        private JarvisCore? _jarvis; // NEW: JARVIS Intelligence Core
        private ReflectionAgent? _reflection; // NEW: Error Recovery
        private System.Windows.Threading.DispatcherTimer _bgTimer;
        private string _lastWindowName = "";
        private DateTime _appStartTime = DateTime.Now;

        private ClipboardService?        _clipboardService;
        private FileWatcherService?      _fileWatcher;
        private GitWatcherService?       _gitWatcher;
        private SystemMetricsService?    _metrics;
        private NotificationService?     _notifications;
        private ChromeBridgeService?     _chromeBridge;
        private CoreMemoryService?       _coreMemory;
        // Phase C-E: data lake + knowledge graph + memory engine
        private DataLakeService?         _dataLake;
        private EventLogService?         _eventLog;
        private TelegramService?         _telegram;
        private KnowledgeGraphService?   _knowledgeGraph;
        private MemoryEngine?            _memoryEngine;

        private FluxBrain? _brain; // NEW: Central intelligence router
        private GeminiTtsService? _tts; // TTS via Gemini Live API
        private bool _isSecondary = false;
        private bool _isMicOn = false;
        private bool _requireWakeWord = false;
        private string _panelName = "Davos";

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

        public MainWindow() : this(false, "Davos") { }

        public MainWindow(bool isSecondary, string startName = "Davos")
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
                        LogMessage("[System] Previous session context restored.");
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
                if (_panelName == "Davos")
                {
                    _panelName = $"Davos Unit-{new Random().Next(10, 99)}";
                    NameBox.Text = _panelName;
                    TitleText.Text = _panelName.ToUpper();
                }
                FadeIn();
            }


            LoadSession();
            SaveSession();
            LoadConfig();
            InitializeServices();
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
                _cortex.StartFocusTracking();
                _gemini = new GeminiService(API_KEY);

                // Build ILLMService — either raw Gemini or ModelRouter with local fallback
                ILLMService llm = _gemini; // default: Gemini only
                if (_settings.EnableLocalModel)
                {
                    var local = new LocalLLMService(_settings.LocalModelUrl, _settings.LocalModelId);
                    llm = new ModelRouter(primary: local, fallback: _gemini, visionModel: _gemini);
                    System.Diagnostics.Debug.WriteLine($"[INIT] ModelRouter active: local={_settings.LocalModelId} @ {_settings.LocalModelUrl}");
                }

                _memory = new MemoryService(llm);

                // NEW: Инициализация Windows Automation
                _automation = new WindowsAutomationAgent();
                _hippocampus = new Hippocampus();

                // 2. Audio — ElevenLabs Scribe v2 Realtime STT (~150ms latency)
                _audioService = new AudioService(_settings.ElevenLabsApiKey, _settings.SttLanguageCodes);

                _inputTimer = new System.Windows.Threading.DispatcherTimer();
                _inputTimer.Tick += InputTimer_Tick;
                _inputTimer.Interval = TimeSpan.FromMilliseconds(20);
                _inputTimer.Start();

                _audioService.OnFinalText += async (text) => await ProcessRequest(text);
                _audioService.OnPartialText += (text) => Dispatcher.Invoke(() => StatusText.Text = text);
                _audioService.OnError += (err) => Dispatcher.Invoke(() => LogMessage($"Audio Error: {err}"));
                _audioService.OnConnected += () => Dispatcher.Invoke(() => LogMessage("ElevenLabs STT: Connected ✓"));

                if (_isMicOn) _ = _audioService.StartContinuousRecording();
                if (!_isSecondary) LogMessage("Flux Core Ready (Vector Memory Online).");

                // NEW: Initialize JARVIS Core
                _codeRunner = new CodeExecutionAgent();
                _reflection = new ReflectionAgent(llm);
                _validator = new ValidatorAgent(llm);
                _jarvis = new JarvisCore(
                    llm,
                    _executor ?? new ExecutionAgent(),
                    _automation,
                    _codeRunner,
                    _cortex,  // NEW: Pass cortex for element detection
                    _hippocampus!,
                    _validator!,
                    _reflection!,  // Failure analysis agent
                    () => _cortex?.GetScreenBase64() ?? "",
                    () => _cortex?.GetActiveWindow() ?? "Unknown",
                    (msg) => Dispatcher.InvokeAsync(() => { _voiceLog.AppendLine(msg); if (LogOverlay.Visibility == Visibility.Visible) LogTextBox.Text = _voiceLog.ToString(); })
                );

                // Passive data collection services
                _fileWatcher              = new FileWatcherService();
                _clipboardService         = new ClipboardService(_cortex);
                _gitWatcher               = new GitWatcherService();
                _metrics                  = new SystemMetricsService();
                _notifications            = new NotificationService();
                _chromeBridge             = new ChromeBridgeService();
                _jarvis.FileWatcher       = _fileWatcher;
                _jarvis.Clipboard         = _clipboardService;
                _jarvis.GitWatcher        = _gitWatcher;
                _jarvis.Metrics           = _metrics;
                _jarvis.Notifications     = _notifications;
                _jarvis.ChromeBridge      = _chromeBridge;

                // === PHASE C-E: Data Lake + Knowledge Graph + Memory Engine ===
                _dataLake        = new DataLakeService();
                _knowledgeGraph  = new KnowledgeGraphService(llm, _dataLake);
                _memoryEngine    = new MemoryEngine(_memory, _dataLake, _knowledgeGraph);

                // Wire DataLake to all passive collection services
                _clipboardService.DataLake  = _dataLake;
                _fileWatcher.DataLake       = _dataLake;
                _notifications.DataLake     = _dataLake;
                _chromeBridge.DataLake      = _dataLake;
                _codeRunner.DataLake        = _dataLake;

                // EventLog service (Phase F1)
                _eventLog          = new EventLogService();
                _eventLog.DataLake = _dataLake;

                // Wire new services to JarvisCore
                _jarvis.DataLake      = _dataLake;
                _jarvis.EventLog      = _eventLog;
                _jarvis.TerminalSource = _codeRunner;
                _jarvis.KnowledgeGraph = _knowledgeGraph;

                // Telegram (Phase F2) — opt-in via settings
                if (_settings.TelegramEnabled
                    && _settings.TelegramApiId > 0
                    && !string.IsNullOrEmpty(_settings.TelegramApiHash))
                {
                    _telegram            = new TelegramService(_settings.TelegramApiId, _settings.TelegramApiHash);
                    _telegram.DataLake   = _dataLake;
                    _telegram.Memory     = _memory;
                    _jarvis.Telegram     = _telegram;
                    _ = Task.Run(() => _telegram.StartAsync()); // non-blocking auth + connect
                    System.Diagnostics.Debug.WriteLine("[INIT] TelegramService starting...");
                }

                // Register clipboard listener on main HWND
                if (!_isSecondary)
                {
                    var hwnd      = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    var hwndSrc   = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
                    _clipboardService.Attach(hwnd);
                    hwndSrc?.AddHook(_clipboardService.HwndHook);
                }

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
                _brain = new FluxBrain(llm, _jarvis, _memory, _hippocampus, _cortex);
                _coreMemory = new CoreMemoryService(llm);
                _brain.SetCoreMemory(_coreMemory);

                // Confidence-based confirmation gates
                _brain.OnConfirmationNeeded = async (q) => await RequestConfirmationAsync(q);
                _jarvis.SetActionConfirmCallback(async (q) => await RequestConfirmationAsync(q));

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

                // Hide/Show Flux window during PC tasks so screenshots capture actual desktop
                // BeginInvoke = non-blocking (prevents deadlock from background thread)
                _brain.OnHideWindow += () => Dispatcher.BeginInvoke(new Action(() =>
                {
                    this.WindowState = System.Windows.WindowState.Minimized;
                }));
                _brain.OnShowWindow += () => Dispatcher.BeginInvoke(new Action(() =>
                {
                    this.WindowState = System.Windows.WindowState.Normal;
                    this.Activate();
                }));

                _brain.Start();
                System.Diagnostics.Debug.WriteLine("[BRAIN] FluxBrain started successfully");

                // TTS: Gemini Live API audio output (opt-in via settings)
                if (_settings.TtsEnabled)
                {
                    _tts = new GeminiTtsService(API_KEY, _settings.TtsVoice);
                    _tts.OnError += (err) => Dispatcher.InvokeAsync(() => LogMessage($"[TTS Error] {err}"));
                    _ = _tts.ConnectAsync();

                    // Speak AI responses aloud
                    _brain.OnMessage += (text, isUser) =>
                    {
                        if (!isUser) _ = _tts.SpeakAsync(text);
                    };

                    // Interrupt speech when user sends a new message
                    // (handled in ProcessRequest via _tts?.StopPlayback())
                }

                _bgTimer = new System.Windows.Threading.DispatcherTimer();
                _bgTimer.Tick += BackgroundMonitor_Tick;
                _bgTimer.Interval = TimeSpan.FromMilliseconds(500);
                _bgTimer.Start();
            }
            catch (Exception ex) { AddMessage($"Init Failed: {ex.Message}", false); }
        }
    }
}
