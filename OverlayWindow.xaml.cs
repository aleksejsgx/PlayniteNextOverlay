using System;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Interop;
using System.Linq;
using System.Diagnostics;
using System.Threading.Tasks;
using Playnite.SDK.Events;

namespace PlayniteGameOverlay
{
    public partial class OverlayWindow : Window
    {
        private readonly Logger _logger;
        private readonly GameStateManager _gameStateManager;
        private readonly BatteryManager _batteryManager;

        private readonly DispatcherTimer clockTimer;
        private DispatcherTimer batteryUpdateTimer;
        private DispatcherTimer controllerTimer;

        private DateTime lastUpTime = DateTime.MinValue;
        private DateTime lastDownTime = DateTime.MinValue;
        private DateTime lastLeftTime = DateTime.MinValue;
        private DateTime lastRightTime = DateTime.MinValue;

        OverlaySettings Settings;

        public bool Active = false;

        public OverlayWindowViewModel ViewModel { get; private set; }

        public OverlayWindow(OverlaySettings settings)
        {
            InitializeComponent();

            Settings = settings;

            _logger = new Logger(settings?.DebugMode ?? false);
            _gameStateManager = new GameStateManager(_logger);
            _batteryManager = new BatteryManager();

            // Initialize ViewModel
            ViewModel = new OverlayWindowViewModel();
            this.DataContext = ViewModel;

            // Connect ViewModel events to handlers
            ViewModel.HideOverlayRequested += () => {
                FocusGameWindow();
                this.Hide(); 
            };
            ViewModel.ShowPlayniteRequested += (fullscreen) => {
                this.Hide();
                OnShowPlayniteRequested?.Invoke(); 
            };
            ViewModel.CloseGameRequested += () => {
                this.Hide();
                CloseGame(); 
            };
            ViewModel.ExecuteShortcutRequested += ExecuteShortcut;
            ViewModel.OpenAchievementListRequested += () => AchievementListRequested?.Invoke();
            ViewModel.CloseAchievementListRequested += CloseAchievementList;
            ViewModel.AchievementList.ItemsReplaced += OnAchievementItemsReplaced;

            // Hold-to-repeat for D-pad / stick scrolling in the achievement list
            listRepeatTimer = new DispatcherTimer(DispatcherPriority.Input);
            listRepeatTimer.Tick += ListRepeatTimer_Tick;

            // Set the window to fullscreen
            this.WindowState = WindowState.Maximized;

            // Initialize and start the clock timer
            clockTimer = new DispatcherTimer();
            clockTimer.Interval = TimeSpan.FromSeconds(1);
            clockTimer.Tick += UpdateClock;
            clockTimer.Start();

            InitializeBatteryDisplay();

            // Disable keyboard navigation
            this.PreviewKeyDown += OverlayWindow_PreviewKeyDown;

            // Initialize buttons and setup from settings
            ViewModel.DebugVisible = settings?.DebugMode ?? false;
            ViewModel.AspectRatio = settings?.AspectRatio ?? AspectRatio.Portrait;
            ViewModel.InitializeShortcutButtons(settings);
        }

        private void InitializeBatteryDisplay()
        {
            if (!_batteryManager.HasBattery)
            {
                ViewModel.HasBattery = false;
            }
            else
            {
                ViewModel.HasBattery = true;
                UpdateBattery(null, EventArgs.Empty);
                batteryUpdateTimer = new DispatcherTimer();
                batteryUpdateTimer.Interval = TimeSpan.FromSeconds(60);
                batteryUpdateTimer.Tick += UpdateBattery;
                batteryUpdateTimer.Start();
            }
        }

        private void UpdateBattery(object sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                var batteryStatus = _batteryManager.GetStatus();
                ViewModel.BatteryPercentage = ((int)batteryStatus.Percentage*100);
            });
        }

        private void UpdateClock(object sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                // Update session time if game is active
                ViewModel.UpdateSessionTime();
                ViewModel.UpdateCurrentTime();
            });
        }

        public void UpdateGameOverlay(GameOverlayData gameData)
        {
            Dispatcher.Invoke(() =>
            {
                ViewModel.UpdateFromGameOverlayData(gameData);

                if (ViewModel.DebugVisible)
                {
                    if (gameData == null)
                    {
                        ViewModel.UpdateDebugInfo("No active game");
                    }
                    else
                    {
                        ViewModel.UpdateDebugInfo($"DEBUG INFO:\n" +
                            $"Game: {gameData.GameName}\n" +
                            $"PID: {gameData.ProcessId}\n" +
                            $"Start Time: {gameData.GameStartTime}");
                    }
                }
            });
        }

        private void CloseGame()
        {
            _gameStateManager.CloseGame(CreateGameOverlayDataFromViewModel());
            this.Hide();
        }

        // Delay between hiding the overlay and sending a keyboard shortcut, so the
        // game window has regained focus (for example an NVIDIA App hotkey).
        private const int KbdShortcutDelayMs = 200;

        private void FocusGameWindow()
        {
            try
            {
                if (ViewModel.IsGameRunning)
                {
                    var proc = FindProcessById(ViewModel.ProcessId);
                    if (proc != null && proc.MainWindowHandle != IntPtr.Zero)
                    {
                        // Restore the window if it's minimized
                        if (WindowHelper.IsIconic(proc.MainWindowHandle))
                        {
                            WindowHelper.ShowWindow(proc.MainWindowHandle, WindowHelper.SW_RESTORE);
                        }
                        // Bring the game window to the front
                        WindowHelper.SetForegroundWindow(proc.MainWindowHandle);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error focusing game window: {ex.Message}");
            }
        }

        private async void ExecuteShortcut(ShortcutButtonViewModel btn)
        {
            try
            {
                switch (btn.Type)
                {
                    case ShortcutType.Gallery:
                        ButtonActions.OpenCapturesFolder(btn.ShortcutCommand);
                        break;
                    case ShortcutType.Browser:
                        ButtonActions.OpenWebBrowser(btn.ShortcutCommand);
                        break;
                    case ShortcutType.KbdShortcut:
                        // hide, give focus back to the game, wait a bit, then execute shortcut
                        this.Hide();
                        FocusGameWindow();
                        await Task.Delay(KbdShortcutDelayMs);
                        ButtonActions.ExecuteKbdShortcut(btn.ShortcutCommand);
                        return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error executing shortcut '{btn?.Title}': {ex.Message}");
            }

            // Return to game after executing other shortcuts
            this.Hide();
        }

        private GameOverlayData CreateGameOverlayDataFromViewModel()
        {
            // Create a GameOverlayData object from the ViewModel
            return new GameOverlayData
            {
                GameName = ViewModel.GameTitle,
                ProcessId = ViewModel.ProcessId,
                GameStartTime = ViewModel.SessionStartTime,
                Playtime = ViewModel.TotalPlaytime,
                CoverImagePath = ViewModel.CoverImagePath
                // Add other properties as needed
            };
        }

        public void ShowOverlay()
        {
            Active = true;
            // Resume timers and immediately update
            ResumeTimers();

            // Update immediately
            UpdateClock(null, EventArgs.Empty);
            UpdateBattery(null, EventArgs.Empty);

            // Show the window
            Dispatcher.Invoke(() =>
            {
                this.Show();
                // Activate the window to bring it to the foreground and set focus
                this.Activate();
                ForceFocusOverlay();
                // Set focus to first button when showing overlay
                ReturnToGameButton.Focus();
            });

        }

        public void ForceFocusOverlay()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            WindowHelper.ForceFocusWindow(hwnd);
        }

        private void ResumeTimers()
        {
            Dispatcher.Invoke(() =>
            {
                clockTimer.Start();

                if (batteryUpdateTimer != null)
                {
                    batteryUpdateTimer.Start();
                }

                if (controllerTimer != null)
                {
                    controllerTimer.Start();
                }
            });
        }

        private void PauseTimers()
        {
            Dispatcher.Invoke(() =>
            {
                clockTimer.Stop();

                if (batteryUpdateTimer != null)
                {
                    batteryUpdateTimer.Stop();
                }

                if (controllerTimer != null)
                {
                    controllerTimer.Stop();
                }
            });
        }

        private Process FindProcessById(int processId)
        {
            try
            {
                return Process.GetProcessById(processId);
            }
            catch
            {
                return null;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            // Close the achievement list first so the plugin resumes a suspended game
            CloseAchievementList();

            // Stop timers
            PauseTimers();

            base.OnClosed(e);
        }

        public new void Hide()
        {
            // Hiding the overlay always closes the achievement list (-> game is resumed)
            CloseAchievementList();

            Active = false;
            // Pause timers when hiding
            PauseTimers();

            // Call base Hide method
            Dispatcher.Invoke(() =>
            {
                base.Hide();
            });
        }

        // Navigation methods for the OverlayWindow class
        private void OverlayWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (ViewModel.IsAchievementListOpen)
            {
                HandleAchievementListKey(e);
                return;
            }

            // List of keys to block
            var blockedKeys = new[]
            {
                System.Windows.Input.Key.Tab,
                System.Windows.Input.Key.Left,
                System.Windows.Input.Key.Right,
                System.Windows.Input.Key.Up,
                System.Windows.Input.Key.Down
            };

            if (blockedKeys.Contains(e.Key))
            {
                e.Handled = true; // Prevent navigation
            }
        }

        // Focus navigation methods for controller support
        public void FocusDown()
        {
            Dispatcher.Invoke(() =>
            {

                UIElement focusedElement = Keyboard.FocusedElement as UIElement;
                if (focusedElement != null)
                {
                    _logger.Log($"Moving focus from {focusedElement.GetType().Name} to next element", "SDL_NAV");
                    focusedElement.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                    _logger.Log($"Focus now on: {(Keyboard.FocusedElement as FrameworkElement)?.Name ?? "unknown"}", "SDL_NAV");
                }
                else
                {
                    _logger.Log("No element currently has focus for next navigation", "SDL_NAV");
                }

            });
}

        public void FocusUp()
        {
            Dispatcher.Invoke(() =>
            {

                UIElement focusedElement = Keyboard.FocusedElement as UIElement;
                if (focusedElement != null)
                {
                    _logger.Log($"Moving focus from {focusedElement.GetType().Name} to previous element", "SDL_NAV");
                    focusedElement.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
                    _logger.Log($"Focus now on: {(Keyboard.FocusedElement as FrameworkElement)?.Name ?? "unknown"}", "SDL_NAV");
                }
                else
                {
                    _logger.Log("No element currently has focus for previous navigation", "SDL_NAV");
                }

            });
        }

        public void FocusLeft()
        {
            Dispatcher.Invoke(() =>
            {

                UIElement focusedElement = Keyboard.FocusedElement as UIElement;
                if (focusedElement != null)
                {
                    _logger.Log($"Moving focus from {focusedElement.GetType().Name} to left element", "SDL_NAV");
                    focusedElement.MoveFocus(new TraversalRequest(FocusNavigationDirection.Left));
                    _logger.Log($"Focus now on: {(Keyboard.FocusedElement as FrameworkElement)?.Name ?? "unknown"}", "SDL_NAV");
                }
                else
                {
                    _logger.Log("No element currently has focus for left navigation", "SDL_NAV");
                }

            });
        }

        public void FocusRight()
        {
            Dispatcher.Invoke(() =>
            {

                UIElement focusedElement = Keyboard.FocusedElement as UIElement;
                if (focusedElement != null)
                {
                    _logger.Log($"Moving focus from {focusedElement.GetType().Name} to right element", "SDL_NAV");
                    focusedElement.MoveFocus(new TraversalRequest(FocusNavigationDirection.Right));
                    _logger.Log($"Focus now on: {(Keyboard.FocusedElement as FrameworkElement)?.Name ?? "unknown"}", "SDL_NAV");
                }
                else
                {
                    _logger.Log("No element currently has focus for right navigation", "SDL_NAV");
                }

            });
        }

        public void ClickFocusedElement()
        {
            Dispatcher.Invoke(() =>
            {
                if (Keyboard.FocusedElement is System.Windows.Controls.Button button)
                {
                    _logger.Log($"Clicking button: {button.Name}", "SDL_NAV");

                    // Try to invoke the Click event handler directly if available
                    if (button.Command != null && button.Command.CanExecute(button.CommandParameter))
                    {
                        button.Command.Execute(button.CommandParameter);
                    }
                    else
                    {
                        // Create a click routed event
                        RoutedEventArgs args = new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent);

                        // Raise the event on the button
                        button.RaiseEvent(args);
                    }
                }
                else
                {
                    _logger.Log($"Focused element is not a button: {Keyboard.FocusedElement?.GetType().Name ?? "null"}", "SDL_NAV");
                }
            });
        }

        #region Achievement list view

        // Event raised when the user activates the achievement panel (plugin loads data and calls OpenAchievementList)
        public event Action AchievementListRequested;
        // Raised whenever the achievement list closes, whatever the reason (plugin resumes the game)
        public event Action AchievementListClosed;

        private readonly DispatcherTimer listRepeatTimer;
        private ControllerInput? heldListButton;
        private int heldListStep;

        private const int RepeatInitialDelayMs = 350;
        private const int RepeatItemIntervalMs = 70;
        private const int RepeatPageIntervalMs = 220;

        public bool IsAchievementListOpen => ViewModel.IsAchievementListOpen;

        /// <summary>Shows the in-overlay list layer for the given achievements (data is loaded by the caller only now).</summary>
        public void OpenAchievementList(string gameName, System.Collections.Generic.IList<AchievementData> achievements)
        {
            Dispatcher.Invoke(() =>
            {
                StopListRepeat();
                ViewModel.GamePauseStatus = null;
                ViewModel.AchievementList.Load(gameName, achievements);
                ViewModel.IsAchievementListOpen = true;
                SelectListIndex(0);
                AchievementListBox.Focus();
                _logger.Log($"Achievement list opened: {ViewModel.AchievementList.TotalCount} items", "ACH_LIST");
            });
        }

        /// <summary>Closes the list layer and returns to the main overlay. Safe to call at any time.</summary>
        public void CloseAchievementList()
        {
            Dispatcher.Invoke(() =>
            {
                StopListRepeat();
                if (!ViewModel.IsAchievementListOpen)
                    return;
                try
                {
                    ViewModel.IsAchievementListOpen = false;
                    ViewModel.GamePauseStatus = null;
                    ViewModel.AchievementList.Clear();
                    if (IsVisible)
                    {
                        if (AchievementPanel.IsVisible) AchievementPanel.Focus();
                        else ReturnToGameButton.Focus();
                    }
                    _logger.Log("Achievement list closed", "ACH_LIST");
                }
                finally
                {
                    AchievementListClosed?.Invoke();
                }
            });
        }

        public void SetGamePauseStatus(string status)
        {
            Dispatcher.Invoke(() => ViewModel.GamePauseStatus = ViewModel.IsAchievementListOpen ? status : null);
        }

        /// <summary>
        /// Controller input while the list is open. Called for presses AND releases (releases stop hold-to-repeat).
        /// D-pad/left stick/right stick up-down: previous/next item (repeats while held);
        /// left/right: page up/down; LB/RB: previous/next filter; X: next filter; Y: reveal/hide secret
        /// descriptions; LT/RT: first/last item; B: back to the main overlay.
        /// </summary>
        public void HandleAchievementListInput(ControllerInput button, bool pressed)
        {
            Dispatcher.Invoke(() =>
            {
                if (!ViewModel.IsAchievementListOpen)
                    return;

                int step;
                if (TryGetNavStep(button, out step))
                {
                    if (pressed)
                    {
                        heldListButton = button;
                        heldListStep = step;
                        MoveListSelection(step);
                        listRepeatTimer.Interval = TimeSpan.FromMilliseconds(RepeatInitialDelayMs);
                        listRepeatTimer.Start();
                    }
                    else if (heldListButton == button)
                    {
                        StopListRepeat();
                    }
                    return;
                }

                if (!pressed)
                    return;

                switch (button)
                {
                    case ControllerInput.B:
                        _logger.Log("B pressed - closing achievement list", "ACH_LIST");
                        CloseAchievementList();
                        break;
                    case ControllerInput.X:
                    case ControllerInput.RightShoulder:
                        ViewModel.AchievementList.CycleFilter(+1);
                        break;
                    case ControllerInput.LeftShoulder:
                        ViewModel.AchievementList.CycleFilter(-1);
                        break;
                    case ControllerInput.Y:
                        ViewModel.AchievementList.RevealHidden = !ViewModel.AchievementList.RevealHidden;
                        break;
                    case ControllerInput.TriggerLeft:
                        SelectListIndex(0);
                        break;
                    case ControllerInput.TriggerRight:
                        SelectListIndex(AchievementListBox.Items.Count - 1);
                        break;
                }
            });
        }

        private bool TryGetNavStep(ControllerInput button, out int step)
        {
            switch (button)
            {
                case ControllerInput.DPadUp:
                case ControllerInput.LeftStickUp:
                case ControllerInput.RightStickUp:
                    step = -1; return true;
                case ControllerInput.DPadDown:
                case ControllerInput.LeftStickDown:
                case ControllerInput.RightStickDown:
                    step = 1; return true;
                case ControllerInput.DPadLeft:
                case ControllerInput.LeftStickLeft:
                    step = -ListPageSize(); return true;
                case ControllerInput.DPadRight:
                case ControllerInput.LeftStickRight:
                    step = ListPageSize(); return true;
                default:
                    step = 0; return false;
            }
        }

        private void ListRepeatTimer_Tick(object sender, EventArgs e)
        {
            if (!ViewModel.IsAchievementListOpen || heldListButton == null)
            {
                StopListRepeat();
                return;
            }
            listRepeatTimer.Interval = TimeSpan.FromMilliseconds(Math.Abs(heldListStep) > 1 ? RepeatPageIntervalMs : RepeatItemIntervalMs);
            // Stops by itself at the top/bottom (also guards against a lost "released" event)
            if (!MoveListSelection(heldListStep))
                StopListRepeat();
        }

        private void StopListRepeat()
        {
            heldListButton = null;
            listRepeatTimer?.Stop();
        }

        private int ListPageSize()
        {
            var h = AchievementListBox.ActualHeight;
            return Math.Max(1, (int)(h / 96) - 1);
        }

        private bool MoveListSelection(int step)
        {
            int count = AchievementListBox.Items.Count;
            if (count == 0) return false;
            int current = AchievementListBox.SelectedIndex;
            int next = current < 0 ? 0 : Math.Max(0, Math.Min(count - 1, current + step));
            if (next == current) return false;
            SelectListIndex(next);
            return true;
        }

        private void SelectListIndex(int index)
        {
            int count = AchievementListBox.Items.Count;
            if (count == 0 || index < 0) return;
            if (index >= count) index = count - 1;
            AchievementListBox.SelectedIndex = index;
            AchievementListBox.ScrollIntoView(AchievementListBox.Items[index]);
        }

        private void OnAchievementItemsReplaced()
        {
            // New filter / data: start from the top
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (ViewModel.IsAchievementListOpen)
                    SelectListIndex(0);
            }));
        }

        /// <summary>Keyboard (e.g. the Rii mini keyboard) while the list is open.</summary>
        private void HandleAchievementListKey(System.Windows.Input.KeyEventArgs e)
        {
            var list = ViewModel.AchievementList;
            switch (e.Key)
            {
                case Key.Up: MoveListSelection(-1); break;
                case Key.Down: MoveListSelection(1); break;
                case Key.PageUp: MoveListSelection(-ListPageSize()); break;
                case Key.PageDown: MoveListSelection(ListPageSize()); break;
                case Key.Home: SelectListIndex(0); break;
                case Key.End: SelectListIndex(AchievementListBox.Items.Count - 1); break;
                case Key.Left: list.CycleFilter(-1); break;
                case Key.Right: list.CycleFilter(1); break;
                case Key.Tab: list.CycleFilter((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1); break;
                case Key.H: list.RevealHidden = !list.RevealHidden; break;
                case Key.Back:
                case Key.Escape:   // normally already handled by the global hook in the plugin
                    CloseAchievementList();
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }

        #endregion

        // Event to request showing Playnite (to be handled by the plugin)
        public event Action OnShowPlayniteRequested;
        // Event to request showing the Overlay (to be handled by the plugin)
        public event Action OnShowOverlayRequested;
    }
}