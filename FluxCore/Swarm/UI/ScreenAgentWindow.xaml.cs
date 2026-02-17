using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FluxCore.Swarm.Agents;
using FluxCore.Swarm.Environment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Keyboard = System.Windows.Input.Keyboard;
using ModifierKeys = System.Windows.Input.ModifierKeys;
using Key = System.Windows.Input.Key;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Brushes = System.Windows.Media.Brushes;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace FluxCore.Swarm.UI
{
    /// <summary>
    /// Tab-based UI for watching ScreenAgent work and chatting with it naturally.
    /// User can see live VM screenshots and ask questions without using commands.
    /// </summary>
    public partial class ScreenAgentWindow : Window
    {
        private readonly ScreenAgent _screenAgent;
        private readonly IScreenEnvironment _screenEnv;
        private readonly GeminiService _gemini;
        private readonly DispatcherTimer _screenshotTimer;
        private readonly List<ChatBubble> _chatHistory = new();
        private CancellationTokenSource? _cts;
        private bool _isPaused;
        private bool _isConnected;

        public event EventHandler<string>? UserMessageSent;
        public event EventHandler? StopRequested;
        public event EventHandler? PauseRequested;

        public ScreenAgentWindow(
            ScreenAgent screenAgent,
            IScreenEnvironment screenEnvironment,
            GeminiService gemini)
        {
            InitializeComponent();

            _screenAgent = screenAgent;
            _screenEnv = screenEnvironment;
            _gemini = gemini;
            _cts = new CancellationTokenSource();

            // Set up screenshot refresh timer (every 1.5 seconds)
            _screenshotTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1500)
            };
            _screenshotTimer.Tick += ScreenshotTimer_Tick;

            // Subscribe to ScreenAgent events
            SubscribeToAgentEvents();

            // Start connecting
            _ = ConnectAsync();
        }

        private void SubscribeToAgentEvents()
        {
            // These would be events on ScreenAgent that notify of status changes
            // For now we'll poll via the timer
        }

        private async Task ConnectAsync()
        {
            try
            {
                LoadingOverlay.Visibility = Visibility.Visible;
                NoScreenshotText.Visibility = Visibility.Collapsed;

                UpdateStatus("Connecting...", "#EBCB8B");

                // Try to ensure the screen environment is running
                await _screenEnv.EnsureRunningAsync(_cts?.Token ?? CancellationToken.None);

                _isConnected = true;
                LoadingOverlay.Visibility = Visibility.Collapsed;

                UpdateStatus("Connected", "#A3BE8C");
                AddAgentMessage("I'm connected and ready to help! You can watch me work here, or just ask me anything.");

                // Start the screenshot refresh timer
                _screenshotTimer.Start();

                // Capture initial screenshot
                await RefreshScreenshotAsync();
            }
            catch (Exception ex)
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                NoScreenshotText.Text = $"Failed to connect: {ex.Message}";
                NoScreenshotText.Visibility = Visibility.Visible;

                UpdateStatus("Connection Failed", "#BF616A");
                AddAgentMessage($"I couldn't connect to the screen environment: {ex.Message}");
            }
        }

        private async void ScreenshotTimer_Tick(object? sender, EventArgs e)
        {
            if (_isPaused || !_isConnected)
                return;

            try
            {
                await RefreshScreenshotAsync();
            }
            catch
            {
                // Silently handle screenshot failures
            }
        }

        private async Task RefreshScreenshotAsync()
        {
            try
            {
                var base64 = await _screenEnv.CaptureScreenshotAsync(_cts?.Token ?? CancellationToken.None);

                if (!string.IsNullOrEmpty(base64))
                {
                    var imageBytes = Convert.FromBase64String(base64);
                    var image = new BitmapImage();

                    using (var ms = new MemoryStream(imageBytes))
                    {
                        image.BeginInit();
                        image.CacheOption = BitmapCacheOption.OnLoad;
                        image.StreamSource = ms;
                        image.EndInit();
                        image.Freeze();
                    }

                    ScreenshotImage.Source = image;
                    NoScreenshotText.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Screenshot refresh failed: {ex.Message}");
            }
        }

        private void UpdateStatus(string status, string colorHex)
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = status;
                StatusLight.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
            });
        }

        public void UpdateCurrentTask(string taskDescription)
        {
            Dispatcher.Invoke(() =>
            {
                CurrentTaskText.Text = taskDescription;
            });
        }

        public void UpdateLastAction(string action)
        {
            Dispatcher.Invoke(() =>
            {
                LastActionText.Text = action;
            });
        }

        #region Chat Functionality

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            SendMessage();
        }

        private void ChatInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                e.Handled = true;
                SendMessage();
            }
        }

        private async void SendMessage()
        {
            var message = ChatInput.Text.Trim();
            if (string.IsNullOrEmpty(message))
                return;

            ChatInput.Text = "";
            ChatInput.Focus();

            // Add user message to chat
            AddUserMessage(message);

            // Notify external handlers
            UserMessageSent?.Invoke(this, message);

            // Process the message with AI
            await ProcessUserMessageAsync(message);
        }

        private async Task ProcessUserMessageAsync(string message)
        {
            try
            {
                // Get current screenshot for context
                string? screenshot = null;
                try
                {
                    screenshot = await _screenEnv.CaptureScreenshotAsync(_cts?.Token ?? CancellationToken.None);
                }
                catch { }

                // Build context-aware prompt
                var prompt = $@"You are a ScreenAgent assistant helping the user with visual tasks in a VM.
The user is watching your screen and asking: ""{message}""

Current context:
- You are operating in an isolated VM environment
- You can see the screen, click, type, and interact with applications
- The user wants natural conversation, not commands

If the user is asking you to do something (like click, type, open something), acknowledge it and describe what you're going to do.
If they're asking a question about what you see, describe what's on screen.
Be helpful, friendly, and conversational.

Respond naturally as if you're a helpful assistant:";

                var history = new List<ChatMessage>
                {
                    new ChatMessage { IsUser = true, Text = prompt }
                };

                var imageParam = !string.IsNullOrEmpty(screenshot) ? $"Base64:{screenshot}" : "";
                var response = await _gemini.ChatWithHistory(history, prompt, imageParam, "", "");

                AddAgentMessage(response);

                // If the message seems like a command, try to execute it
                if (IsActionRequest(message))
                {
                    await TryExecuteUserRequestAsync(message, response);
                }
            }
            catch (Exception ex)
            {
                AddAgentMessage($"Sorry, I encountered an error: {ex.Message}");
            }
        }

        private bool IsActionRequest(string message)
        {
            var lowerMessage = message.ToLower();
            return lowerMessage.Contains("click") ||
                   lowerMessage.Contains("type") ||
                   lowerMessage.Contains("open") ||
                   lowerMessage.Contains("close") ||
                   lowerMessage.Contains("try") ||
                   lowerMessage.Contains("can you") ||
                   lowerMessage.Contains("please") ||
                   lowerMessage.Contains("go to") ||
                   lowerMessage.Contains("scroll");
        }

        private async Task TryExecuteUserRequestAsync(string userMessage, string aiResponse)
        {
            // This would parse the user's natural language request and execute the appropriate action
            // For now, we just update the status to show we're processing it
            UpdateLastAction($"Processing: {userMessage}");

            // The actual execution would be handled by the ScreenAgent's queue
            // We could enqueue a task here based on the user's request
        }

        private void AddUserMessage(string message)
        {
            Dispatcher.Invoke(() =>
            {
                var bubble = CreateChatBubble(message, true);
                ChatMessages.Children.Add(bubble);
                _chatHistory.Add(new ChatBubble { Message = message, IsUser = true });
                ScrollToBottom();
            });
        }

        public void AddAgentMessage(string message)
        {
            Dispatcher.Invoke(() =>
            {
                var bubble = CreateChatBubble(message, false);
                ChatMessages.Children.Add(bubble);
                _chatHistory.Add(new ChatBubble { Message = message, IsUser = false });
                ScrollToBottom();
            });
        }

        private Border CreateChatBubble(string message, bool isUser)
        {
            var border = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isUser ? "#4C566A" : "#3B4252")),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(isUser ? 50 : 8, 4, isUser ? 8 : 50, 4),
                HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                MaxWidth = 600
            };

            var stackPanel = new StackPanel();

            // Sender label
            var senderLabel = new TextBlock
            {
                Text = isUser ? "You" : "ScreenAgent",
                FontWeight = FontWeights.SemiBold,
                FontSize = 11,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isUser ? "#88C0D0" : "#A3BE8C")),
                Margin = new Thickness(0, 0, 0, 4)
            };

            // Message text
            var messageText = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.White,
                FontSize = 14
            };

            stackPanel.Children.Add(senderLabel);
            stackPanel.Children.Add(messageText);
            border.Child = stackPanel;

            return border;
        }

        private void ScrollToBottom()
        {
            ChatScrollViewer.ScrollToEnd();
        }

        #endregion

        #region Control Buttons

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            _isPaused = !_isPaused;

            if (_isPaused)
            {
                PauseButton.Content = "▶ Resume";
                UpdateStatus("Paused", "#EBCB8B");
                _screenshotTimer.Stop();
            }
            else
            {
                PauseButton.Content = "⏸ Pause";
                UpdateStatus("Active", "#A3BE8C");
                _screenshotTimer.Start();
            }

            PauseRequested?.Invoke(this, EventArgs.Empty);
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Are you sure you want to stop the ScreenAgent?",
                "Stop ScreenAgent",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _screenshotTimer.Stop();
                _cts?.Cancel();
                UpdateStatus("Stopped", "#BF616A");
                AddAgentMessage("I've been stopped. Goodbye!");

                StopRequested?.Invoke(this, EventArgs.Empty);
                Close();
            }
        }

        #endregion

        protected override void OnClosed(EventArgs e)
        {
            _screenshotTimer.Stop();
            _cts?.Cancel();
            _cts?.Dispose();
            base.OnClosed(e);
        }

        private class ChatBubble
        {
            public string Message { get; set; } = "";
            public bool IsUser { get; set; }
        }
    }
}
