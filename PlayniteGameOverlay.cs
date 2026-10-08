using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Windows.Forms;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System.ComponentModel;

namespace PlayniteGameOverlay
{
    public class PlayniteGameOverlay : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private OverlaySettings settings;

        private GameOverlayData GameOverlayData;

        private OverlayWindow overlayWindow;
        private IPlayniteAPI playniteAPI;
        private GlobalKeyboardHook keyboardHook;

        private ControllerState controllerState = new ControllerState();

        public override Guid Id { get; } = Guid.Parse("fc70f846-3640-46f0-9f86-5f0b991ca4aa");

        DateTime gameStarted;

        // Overlay views (achievement list) + game pause
        private readonly GameProcessSuspender suspender = new GameProcessSuspender(logger);
        private System.Windows.Threading.DispatcherTimer suspendWatchdog;
        private Game overlayGame;                    // game the overlay was last shown/started for
        private int overlayGameProcessId = -1;       // game process detected by the overlay (FindRunningGameProcess)
        private int lastStartedProcessId = -1;       // OnGameStartedEventArgs.StartedProcessId
        private int foregroundPidBeforeOverlay = -1; // owner of the foreground window right before the overlay opened

        // Achievement integrations
        // Preferred: "Playnite Achievements" (Justin Delano), read-only from its SQLite cache.
        // Fallback: SuccessStory JSON files.
        private bool isPlayniteAchievementsAvailable = false;
        private bool isSuccessStoryAvailable = false;
        private Guid successStoryId = Guid.Parse("cebe6d32-8c46-4459-b993-5a5189d60788"); // SuccessStory plugin ID

        private void CheckAchievementProviders()
        {
            try
            {
                var plugins = playniteAPI.Addons.Plugins;
                isPlayniteAchievementsAvailable = plugins.Any(p => p.Id == PlayniteAchievementsReader.PluginId);
                isSuccessStoryAvailable = plugins.Any(p => p.Id == successStoryId);

                if (isPlayniteAchievementsAvailable)
                {
                    PlayniteAchievementsReader.PreloadNative(playniteAPI.Paths.ApplicationPath, m => logger.Warn(m));
                }

                log($"Achievement providers: PlayniteAchievements={isPlayniteAchievementsAvailable}, SuccessStory={isSuccessStoryAvailable}");
            }
            catch (Exception ex)
            {
                log($"Error checking for achievement plugins: {ex.Message}", "ERROR");
                isPlayniteAchievementsAvailable = false;
                isSuccessStoryAvailable = false;
            }
        }


        public override void OnControllerButtonStateChanged(OnControllerButtonStateChangedArgs args)
        {
            var mostRecentPress = controllerState.Update(args);
            var startBackCombo = false;
            var guideActivated = false;

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                if (overlayWindow != null && overlayWindow.IsActive)
                {
                    // Log all controller actions
                    log($"Window Recieved {args.State.ToString()}: {args.Button.ToString()}", "SDL_INPUT");

                    // Achievement list view has its own controls (needs releases too, for hold-to-repeat)
                    if (overlayWindow.IsAchievementListOpen)
                    {
                        overlayWindow.HandleAchievementListInput(args.Button, args.State == ControllerInputState.Pressed);
                        return;
                    }

                    // ignore releases
                    if (mostRecentPress == null)
                        return;

                    switch (mostRecentPress)
                    {
                        case ControllerInput.DPadUp:
                        case ControllerInput.LeftStickUp:
                            log("Navigating UP", "SDL_NAV");
                            overlayWindow.FocusUp();
                            break;
                        case ControllerInput.DPadDown:
                        case ControllerInput.LeftStickDown:
                            log("Navigating DOWN", "SDL_NAV");
                            overlayWindow.FocusDown();
                            break;
                        case ControllerInput.DPadLeft:
                        case ControllerInput.LeftStickLeft:
                            log("Navigating LEFT", "SDL_NAV");
                            overlayWindow.FocusLeft();
                            break;
                        case ControllerInput.DPadRight:
                        case ControllerInput.LeftStickRight:
                            log("Navigating RIGHT", "SDL_NAV");
                            overlayWindow.FocusRight();
                            break;
                        case ControllerInput.A:
                            log("Button A pressed - clicking focused element", "SDL_NAV");
                            overlayWindow.ClickFocusedElement();
                            break;
                        case ControllerInput.B:
                            log("Button B pressed - Hiding overlay", "SDL_NAV");
                            overlayWindow.Hide();
                            break;
                    }
                }
                else
                {
                    log($"Background Listener Recieved {args.State.ToString()}: {args.Button.ToString()}", "SDL_INPUT");
                    // We only want to process pressed events, not repeated or released
                    if (mostRecentPress == null)
                        return;

                    // For Start+Back combination or Guide button handling
                    if (mostRecentPress == ControllerInput.Start || mostRecentPress == ControllerInput.Back || mostRecentPress == ControllerInput.Guide)
                    {
                        // Check for Start+Back combination
                        startBackCombo = Settings.ControllerShortcut == ControllerShortcut.StartBack &&
                                             controllerState.ButtonStart && controllerState.ButtonBack;

                        // Check for Guide press
                        guideActivated = Settings.ControllerShortcut == ControllerShortcut.Guide &&
                                             controllerState.ButtonGuide;

                        log($"sb:{startBackCombo}, g:{guideActivated}");
                        // Process the shortcut if either condition is met
                        if (startBackCombo || guideActivated)
                        {
                            // Use Application.Current.Dispatcher to ensure UI operations happen on the UI thread
                            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                var runningGame = playniteAPI.Database.Games.FirstOrDefault(g => g.IsRunning);
                                if (runningGame != null)
                                {
                                    if (overlayWindow.IsVisible)
                                        overlayWindow.Hide();
                                    else
                                        ShowGameOverlay(runningGame);
                                }
                                else
                                {
                                    ShowPlaynite(true);
                                }
                            }));
                        }
                    }


                }
            });
        }

        public PlayniteGameOverlay(IPlayniteAPI api) : base(api)
        {
            playniteAPI = api;
            Properties = new GenericPluginProperties { HasSettings = true };

            // Safety nets: never leave a game suspended if Playnite exits or crashes.
            AppDomain.CurrentDomain.ProcessExit += (s, e) => suspender.ResumeAll("Playnite process exit");
            AppDomain.CurrentDomain.UnhandledException += (s, e) => suspender.ResumeAll("unhandled exception");
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            logger.Info("Starting Overlay Extension...");

            // Check which achievement plugins are installed (Playnite Achievements / SuccessStory)
            CheckAchievementProviders();

            // Initialize overlay window
            overlayWindow = new OverlayWindow(Settings);
            overlayWindow.Hide();

            // Set up show Playnite handler + achievement list events
            WireOverlayWindow(overlayWindow);

            try
            {
                System.Windows.Application.Current.DispatcherUnhandledException +=
                    (s, e) => suspender.ResumeAll("dispatcher unhandled exception");
            }
            catch (Exception ex)
            {
                logger.Warn($"Could not hook DispatcherUnhandledException: {ex.Message}");
            }

            // Initialize global keyboard hook
            keyboardHook = new GlobalKeyboardHook();
            keyboardHook.KeyPressed += OnKeyPressed;
        }

        public void ReloadOverlay(OverlaySettings settings)
        {
            suspender.ResumeAll("overlay reloaded (settings saved)");
            overlayWindow.Close(); //close old window
            overlayWindow = new OverlayWindow(settings != null ? settings : Settings); //open new one
            overlayWindow.Hide();
            if (GameOverlayData != null)
            {
                overlayWindow.UpdateGameOverlay(GameOverlayData);
            }

            // Set up show Playnite handler + achievement list events again
            WireOverlayWindow(overlayWindow);
        }

        private void WireOverlayWindow(OverlayWindow window)
        {
            window.OnShowPlayniteRequested += ShowPlaynite;
            window.AchievementListRequested += OnAchievementListRequested;
            window.AchievementListClosed += OnAchievementListClosed;
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            logger.Info("Stopping Overlay...");

            // Never leave a game frozen
            suspender.ResumeAll("Playnite stopping");
            suspendWatchdog?.Stop();

            // Cleanup resources
            overlayWindow?.Close();
            keyboardHook?.Dispose();
        }

        public override void Dispose()
        {
            // Extension unload: last chance to resume a suspended game
            suspender.ResumeAll("extension dispose");
            base.Dispose();
        }

        #region Achievement list view + game pause

        private void OnAchievementListRequested()
        {
            var game = overlayGame ?? playniteAPI.Database.Games.FirstOrDefault(g => g.IsRunning);
            if (game == null)
            {
                logger.Warn("Achievement list requested but no game is associated with the overlay.");
                return;
            }

            List<AchievementData> achievements;
            var sw = Stopwatch.StartNew();
            try
            {
                achievements = GetGameAchievements(game) ?? new List<AchievementData>();
            }
            catch (Exception ex)
            {
                logger.Error($"Error loading achievement list for {game.Name}: {ex.Message}");
                achievements = new List<AchievementData>();
            }
            sw.Stop();
            logger.Info($"Achievement list opened for {game.Name}: {achievements.Count} achievements ({achievements.Count(a => a.IsUnlocked)} unlocked), loaded in {sw.ElapsedMilliseconds} ms");

            overlayWindow.OpenAchievementList(game.Name, achievements);

            if (Settings.SuspendGameInOverlayViews)
            {
                overlayWindow.SetGamePauseStatus("Pausing game...");
                // Let the list render first, then freeze the game.
                System.Windows.Application.Current.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => SuspendGameForOverlayView(game)));
            }
            else
            {
                overlayWindow.SetGamePauseStatus(null);
                logger.Info("Game pause for overlay views is disabled in settings; game keeps running.");
            }
        }

        private void OnAchievementListClosed()
        {
            suspender.ResumeAll("achievement list closed");
            suspendWatchdog?.Stop();
        }

        private void SuspendGameForOverlayView(Game game)
        {
            try
            {
                // The view may already have been closed again before this deferred call ran.
                if (overlayWindow == null || !overlayWindow.IsVisible || !overlayWindow.IsAchievementListOpen)
                {
                    logger.Info("[Overlay suspend] View closed before the game could be paused; nothing to do.");
                    return;
                }

                string source;
                var pids = GetGameProcessesToSuspend(game, out source);
                if (pids.Count == 0)
                {
                    logger.Warn($"[Overlay suspend] No game process found for {game.Name}; the game keeps running.");
                    overlayWindow.SetGamePauseStatus("Game not paused (process not found)");
                    return;
                }

                logger.Info($"[Overlay suspend] Pausing {game.Name}: candidate PIDs {string.Join(", ", pids)} (source: {source})");
                int n = suspender.Suspend(pids, $"achievement list open ({source})", playniteAPI.Paths.ApplicationPath);
                if (n > 0)
                {
                    overlayWindow.SetGamePauseStatus("Game paused");
                    StartSuspendWatchdog();
                }
                else
                {
                    overlayWindow.SetGamePauseStatus("Game not paused (see log)");
                }
            }
            catch (Exception ex)
            {
                logger.Error($"[Overlay suspend] Unexpected error while pausing the game: {ex}");
                suspender.ResumeAll("error while suspending");
                overlayWindow?.SetGamePauseStatus("Game not paused (error)");
            }
        }

        /// <summary>
        /// Picks the root game process and returns it plus its descendants. Order of preference:
        ///  1. the process the overlay itself detected for this game (same detection used by "Exit Game"),
        ///     accepted only if its exe is inside the game's install folder, it is part of the
        ///     Playnite-started process tree, or it owned the foreground window before the overlay opened;
        ///  2. the process tree Playnite started (OnGameStarted StartedProcessId, e.g. emulators);
        ///  3. the process that owned the foreground window right before the overlay opened.
        /// Each PID is then filtered again by GameProcessSuspender's safety rules.
        /// </summary>
        private List<int> GetGameProcessesToSuspend(Game game, out string source)
        {
            source = null;
            int root = -1;

            if (GameProcessSuspender.IsAlive(overlayGameProcessId))
            {
                var path = GameProcessSuspender.TryGetProcessPath(overlayGameProcessId);
                bool inInstallDir = GameProcessSuspender.IsUnderDirectory(path, game?.InstallDirectory);
                bool inStartedTree = lastStartedProcessId > 0 && GameProcessSuspender.IsAlive(lastStartedProcessId) &&
                                     GetProcessTree(lastStartedProcessId).Any(p => p.Id == overlayGameProcessId);
                bool wasForeground = overlayGameProcessId == foregroundPidBeforeOverlay;
                if (inInstallDir || inStartedTree || wasForeground)
                {
                    root = overlayGameProcessId;
                    source = $"overlay-detected game process (installDir={inInstallDir}, startedTree={inStartedTree}, foreground={wasForeground})";
                }
                else
                {
                    logger.Info($"[Overlay suspend] Detected process {overlayGameProcessId} ({path}) is not in the install folder, the started tree or the foreground; ignoring it.");
                }
            }

            if (root <= 0 && GameProcessSuspender.IsAlive(lastStartedProcessId))
            {
                root = lastStartedProcessId;
                source = "process tree started by Playnite";
            }

            if (root <= 0 && GameProcessSuspender.IsAlive(foregroundPidBeforeOverlay))
            {
                root = foregroundPidBeforeOverlay;
                source = "foreground process before the overlay opened";
            }

            if (root <= 0)
                return new List<int>();

            var tree = GetProcessTree(root);
            var pids = new List<int> { root };
            pids.AddRange(tree.Select(p => p.Id).Where(id => id != root));
            return pids.Distinct().ToList();
        }

        private void StartSuspendWatchdog()
        {
            if (suspendWatchdog == null)
            {
                // Safety net: if the view is gone for any reason (missed event, overlay hidden by some other path),
                // resume within ~2 seconds.
                suspendWatchdog = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                suspendWatchdog.Tick += (s, e) =>
                {
                    if (!suspender.HasSuspendedProcesses)
                    {
                        suspendWatchdog.Stop();
                        return;
                    }
                    if (overlayWindow == null || !overlayWindow.IsVisible || !overlayWindow.IsAchievementListOpen)
                    {
                        suspender.ResumeAll("watchdog: overlay view no longer open");
                        suspendWatchdog.Stop();
                    }
                };
            }
            suspendWatchdog.Start();
        }

        private static int GetForegroundProcessId()
        {
            try
            {
                var hwnd = WindowHelper.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return -1;
                uint pid;
                WindowHelper.GetWindowThreadProcessId(hwnd, out pid);
                return (int)pid;
            }
            catch
            {
                return -1;
            }
        }

        #endregion

        private void OnKeyPressed(Keys key, bool altPressed)
        {
            // Alt + ` (backtick) to toggle overlay
            if (altPressed && key == Keys.Oem3)
            {
                var runningGame = playniteAPI.Database.Games.FirstOrDefault(g => g.IsRunning);

                if (runningGame != null)
                {
                    if (overlayWindow.IsVisible)
                        overlayWindow.Hide();
                    else
                        ShowGameOverlay(runningGame);
                }
                else
                {
                    ShowPlaynite();
                }
            }

            // Escape: close the achievement list if open, otherwise hide the overlay
            if (key == Keys.Escape && overlayWindow.IsVisible)
            {
                if (overlayWindow.IsAchievementListOpen)
                    overlayWindow.CloseAchievementList();
                else
                    overlayWindow.Hide();
            }
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            try
            {
                gameStarted = DateTime.Now;
                overlayGame = args.Game;
                lastStartedProcessId = args.StartedProcessId;
                var gameOverlayData = CreateGameOverlayData(args.Game, args.StartedProcessId, gameStarted);
                GameOverlayData = gameOverlayData;
                overlayWindow.UpdateGameOverlay(gameOverlayData);
            }
            catch (Exception ex)
            {
                logger.Error($"Error preparing game overlay data: {ex}");
            }
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            suspender.ResumeAll("game stopped");
            overlayWindow.CloseAchievementList();
            overlayWindow.UpdateGameOverlay(null);
            GameOverlayData = null;
            overlayGameProcessId = -1;
            lastStartedProcessId = -1;
        }

        private void ShowGameOverlay(Game game)
        {
            //if (game == null) return;

            // Remember who had the foreground before we take it (fallback for the game pause)
            var fg = GetForegroundProcessId();
            foregroundPidBeforeOverlay = (fg > 0 && fg != Process.GetCurrentProcess().Id) ? fg : -1;

            overlayGame = game;
            overlayGameProcessId = FindRunningGameProcess(game, null)?.Id ?? -1;
            var gameOverlayData = CreateGameOverlayData(game, overlayGameProcessId > 0 ? overlayGameProcessId : (int?)null, gameStarted);
            overlayWindow.UpdateGameOverlay(gameOverlayData);
            overlayWindow.ShowOverlay();
        }

        private GameOverlayData CreateGameOverlayData(Game game, int? processId, DateTime StartTime)
        {
            if (game == null) return null;

            var achievements = GetGameAchievements(game);

            return new GameOverlayData
            {
                GameName = game.Name,
                ProcessId = processId ?? -1,
                GameStartTime = StartTime,
                Playtime = TimeSpan.FromSeconds(game.Playtime),
                CoverImagePath = GetFullCoverImagePath(game),
                Achievements = achievements
            };
        }

        private List<AchievementData> GetGameAchievements(Game game)
        {
            var achievements = new List<AchievementData>();
            if (game == null)
                return achievements;

            log($"Retrieving achievements for game {game.Name} (ID: {game.Id}, PlayniteAchievements: {isPlayniteAchievementsAvailable}, SuccessStory: {isSuccessStoryAvailable})");

            // 1) Playnite Achievements (preferred)
            if (isPlayniteAchievementsAvailable)
            {
                try
                {
                    var pa = PlayniteAchievementsReader.GetAchievements(
                        playniteAPI.Paths.ExtensionsDataPath, game.Id, m => logger.Warn(m));
                    if (pa != null && pa.Count > 0)
                    {
                        logger.Info($"Retrieved {pa.Count} achievements ({pa.Count(a => a.IsUnlocked)} unlocked) for game {game.Name} from Playnite Achievements");
                        return pa;
                    }
                }
                catch (Exception ex)
                {
                    logger.Error($"Error retrieving achievements from Playnite Achievements: {ex.Message}");
                }
            }

            // 2) SuccessStory (fallback)
            if (!isSuccessStoryAvailable)
                return achievements;

            try
            {
                // Find SuccessStory's data directory
                string successStoryDir = Path.Combine(
                    playniteAPI.Paths.ExtensionsDataPath, successStoryId.ToString(), "SuccessStory");

                // Look for a file containing achievements for this game
                string achievementsFile = Path.Combine(successStoryDir, $"{game.Id}.json");
                if (System.IO.File.Exists(achievementsFile))
                {
                    string achievementsJson = System.IO.File.ReadAllText(achievementsFile);
                    achievements = ParseSuccessStoryData(achievementsJson);
                }

                logger.Info($"Retrieved {achievements.Count} achievements for game {game.Name} from SuccessStory");
            }
            catch (Exception ex)
            {
                logger.Error($"Error retrieving achievements from SuccessStory: {ex.Message}");
            }

            return achievements;
        }

        private string GetFullCoverImagePath(Game game)
        {
            if (string.IsNullOrEmpty(game.CoverImage))
                return null;

            try
            {
                return playniteAPI.Database.GetFullFilePath(game.CoverImage);
            }
            catch (Exception ex)
            {
                logger.Error($"Error getting cover image path: {ex}");
                return null;
            }
        }

        private static Process FindRunningGameProcess(Game game, int? pid)
        {
            if (game == null) return null;

            try
            {
                // Get process tree starting from the provided PID
                var processTree = GetProcessTree(pid);

                // Get all executable files in the game installation directory and subdirectories
                var gameExecutables = GetGameExecutables(game);

                // Get the game name words for title comparison
                string gameName = game.Name;
                string[] gameNameWords = gameName.ToLower().Split(new char[] { ' ', '-', '_', ':', '.', '(', ')', '[', ']' },
                    StringSplitOptions.RemoveEmptyEntries);

                Debug.WriteLine($"Game name for matching: {gameName}, split into {gameNameWords.Length} words");
                Debug.WriteLine($"Process tree contains {processTree.Count} processes");

                // Filter process tree to only include processes with main windows and sufficient memory
                var candidateProcesses = processTree.Where(p =>
                {
                    try
                    {
                        return !p.HasExited &&
                               p.MainWindowHandle != IntPtr.Zero &&
                               !string.IsNullOrEmpty(p.MainWindowTitle) &&
                               p.WorkingSet64 > 100 * 1024 * 1024;
                    }
                    catch
                    {
                        return false;
                    }
                }).ToList();

                Debug.WriteLine($"Found {candidateProcesses.Count} candidate processes in tree");

                var candidates = new List<Process>();
                var nameMatchCandidates = new List<Process>();
                var titleMatchCandidates = new List<(Process Process, int MatchCount)>();
                var inaccessibleCandidates = new List<Process>();

                foreach (var p in candidateProcesses)
                {
                    try
                    {
                        // Check if process name matches any executable in the game folder
                        bool nameMatches = gameExecutables.Any(exe =>
                            string.Equals(exe, p.ProcessName, StringComparison.OrdinalIgnoreCase));

                        // Check window title for matches with game name
                        int titleMatchScore = CalculateTitleMatchScore(p, gameNameWords);

                        try
                        {
                            // Try to access the module info
                            var modulePath = p.MainModule.FileName;
                            if (modulePath.IndexOf(game.InstallDirectory, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                candidates.Add(p);
                            }
                            else if (nameMatches)
                            {
                                nameMatchCandidates.Add(p);
                            }
                            else if (titleMatchScore > 0)
                            {
                                titleMatchCandidates.Add((p, titleMatchScore));
                            }
                        }
                        catch
                        {
                            // Can't access module info (likely due to 32/64 bit mismatch)
                            if (nameMatches)
                            {
                                nameMatchCandidates.Add(p);
                            }
                            else if (titleMatchScore > 0)
                            {
                                titleMatchCandidates.Add((p, titleMatchScore));
                            }
                            else
                            {
                                inaccessibleCandidates.Add(p);
                            }
                        }
                    }
                    catch { /* Skip processes we can't access at all */ }
                }

                // Priority order: direct path match, window title match, process name match, then best guess
                var bestMatch = FindBestMatch(candidates, titleMatchCandidates, nameMatchCandidates, inaccessibleCandidates, pid);

                return bestMatch;
            }
            catch (Exception ex)
            {
                logger.Error($"Error finding game process: {ex.Message}");
                return null;
            }
        }

        private static List<Process> GetProcessTree(int? rootPid)
        {
            var processTree = new List<Process>();

            if (rootPid == null || rootPid <= 0)
            {
                Debug.WriteLine("No root PID provided, using all processes with main windows");
                // Fallback to all processes with main windows if no PID provided
                return Process.GetProcesses()
                    .Where(p =>
                    {
                        try
                        {
                            return p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(p.MainWindowTitle);
                        }
                        catch
                        {
                            return false;
                        }
                    })
                    .ToList();
            }

            try
            {
                // Start with the root process
                var rootProcess = Process.GetProcessById(rootPid.Value);
                if (rootProcess != null && !rootProcess.HasExited)
                {
                    processTree.Add(rootProcess);
                    Debug.WriteLine($"Added root process: {rootProcess.ProcessName} (ID: {rootProcess.Id})");
                }
            }
            catch (Exception ex)
            {
                logger.Warn($"Could not find root process with ID {rootPid}: {ex.Message}");
            }

            // Get all child processes recursively
            var allProcesses = Process.GetProcesses();
            var processesById = allProcesses.ToDictionary(p => p.Id, p => p);
            var childProcesses = GetChildProcesses(rootPid.Value, processesById);

            processTree.AddRange(childProcesses);

            Debug.WriteLine($"Process tree built with {processTree.Count} total processes");
            return processTree;
        }

        private static List<Process> GetChildProcesses(int parentId, Dictionary<int, Process> processesById)
        {
            var children = new List<Process>();

            try
            {
                // Use P/Invoke to get child processes via Windows API
                foreach (var process in processesById.Values)
                {
                    try
                    {
                        if (!process.HasExited && GetParentProcessId(process.Id) == parentId)
                        {
                            children.Add(process);
                            Debug.WriteLine($"Added child process: {process.ProcessName} (ID: {process.Id})");

                            // Recursively get grandchildren
                            children.AddRange(GetChildProcesses(process.Id, processesById));
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error checking process {process.Id}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error getting child processes for PID {parentId}: {ex.Message}");
            }

            return children;
        }

        private static int GetParentProcessId(int processId)
        {
            try
            {
                var handle = OpenProcess(ProcessAccessFlags.QueryInformation, false, processId);
                if (handle == IntPtr.Zero)
                    return -1;

                try
                {
                    var pbi = new PROCESS_BASIC_INFORMATION();
                    int returnLength;
                    int status = NtQueryInformationProcess(handle, 0, ref pbi, Marshal.SizeOf(pbi), out returnLength);

                    if (status == 0)
                        return pbi.InheritedFromUniqueProcessId.ToInt32();
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error getting parent PID for process {processId}: {ex.Message}");
            }

            return -1;
        }

        private static List<string> GetGameExecutables(Game game)
        {
            var gameExecutables = new List<string>();
            try
            {
                gameExecutables = Directory.GetFiles(game.InstallDirectory, "*.exe", SearchOption.AllDirectories)
                    .Select(path => Path.GetFileNameWithoutExtension(path))
                    .ToList();

                // Add common variations
                var additionalNames = new List<string>();
                foreach (var exe in gameExecutables)
                {
                    additionalNames.Add(exe.Replace("-", ""));
                    additionalNames.Add(exe.Replace("_", ""));
                    additionalNames.Add(exe.Replace(" ", ""));
                    additionalNames.Add(exe.Replace("-", " "));
                    additionalNames.Add(exe.Replace("_", " "));
                }
                gameExecutables.AddRange(additionalNames);

                Debug.WriteLine($"Found {gameExecutables.Count} potential game executables in {game.InstallDirectory}");
            }
            catch (Exception ex)
            {
                logger.Error($"Error scanning game directory: {ex.Message}");
            }

            return gameExecutables;
        }

        private static int CalculateTitleMatchScore(Process process, string[] gameNameWords)
        {
            if (process.MainWindowHandle == IntPtr.Zero || string.IsNullOrEmpty(process.MainWindowTitle))
                return 0;

            string[] windowTitleWords = process.MainWindowTitle.ToLower().Split(
                new char[] { ' ', '-', '_', ':', '.', '(', ')', '[', ']' },
                StringSplitOptions.RemoveEmptyEntries);

            return gameNameWords.Count(gameWord =>
                windowTitleWords.Any(titleWord => titleWord.Contains(gameWord) || gameWord.Contains(titleWord)));
        }

        private static Process FindBestMatch(
            List<Process> candidates,
            List<(Process Process, int MatchCount)> titleMatchCandidates,
            List<Process> nameMatchCandidates,
            List<Process> inaccessibleCandidates,
            int? originalPid)
        {
            if (candidates.Count > 0)
            {
                var bestMatch = candidates.OrderByDescending(p => p.WorkingSet64).First();
                Debug.WriteLine($"Found process with matching path: {bestMatch.ProcessName} (ID: {bestMatch.Id})");
                return bestMatch;
            }


            if (nameMatchCandidates.Count > 0)
            {
                var bestMatch = nameMatchCandidates.OrderByDescending(p => p.WorkingSet64).First();
                Debug.WriteLine($"Found process with matching name: {bestMatch.ProcessName} (ID: {bestMatch.Id})");
                return bestMatch;
            }

            if (titleMatchCandidates.Count > 0)
            {
                var bestMatch = titleMatchCandidates.OrderByDescending(t => t.MatchCount)
                                                    .ThenByDescending(t => t.Process.WorkingSet64)
                                                    .First().Process;
                Debug.WriteLine($"Found process with matching window title: {bestMatch.ProcessName} (ID: {bestMatch.Id}, Title: {bestMatch.MainWindowTitle})");
                return bestMatch;
            }

            if (inaccessibleCandidates.Count > 0)
            {
                var processFromPlaynite = inaccessibleCandidates.FirstOrDefault(p => p.Id == originalPid);
                if (processFromPlaynite != null)
                {
                    Debug.WriteLine($"Found original process in tree: {processFromPlaynite.ProcessName} (ID: {processFromPlaynite.Id})");
                    return processFromPlaynite;
                }
            }

            return null;
        }

        private void log(string msg, string tag = "DEBUG")
        {
            if (true)
            {
                Debug.WriteLine("GameOverlay[" + tag + "]: " + msg);
            }
            logger.Debug(msg);
        }

        private void ShowPlaynite()
        {
            ShowPlaynite(false);
        }
        private void ShowPlaynite(bool forceFullscreen = false)
        {
            try
            {
                // If fullscreen is forced, just launch it. It will either focus the existing app or switch to fullscreen
                if (forceFullscreen)
                {
                    Process.Start(Path.Combine(playniteAPI.Paths.ApplicationPath, "Playnite.FullscreenApp.exe"));
                    return;
                }
                // Try desktop first
                Process[] processes = Process.GetProcessesByName("Playnite.DesktopApp");
                if (processes.Length > 0)
                {
                    Process.Start(Path.Combine(playniteAPI.Paths.ApplicationPath, "Playnite.DesktopApp.exe"));
                    return;
                }
                // Then try fullscreen
                processes = Process.GetProcessesByName("Playnite.FullscreenApp");
                if (processes.Length > 0)
                {
                    Process.Start(Path.Combine(playniteAPI.Paths.ApplicationPath, "Playnite.FullscreenApp.exe"));
                    return;
                }
                else
                {
                    log("Could not find any Playnite process to activate", "WARNING");
                }
            }
            catch (Exception ex)
            {
                logger.Error($"Error showing Playnite: {ex}");
            }
        }

        // Method to parse the JSON file and convert to your AchievementData format
        private List<AchievementData> ParseSuccessStoryData(string jsonData)
        {
            var achievements = new List<AchievementData>();

            try
            {

                var successStoryData = JsonSerializer.Deserialize<SuccessStoryData>(jsonData);

                if (successStoryData?.Items == null)
                {
                    logger.Error("Failed to parse SuccessStory data: Items is null");
                    return achievements;
                }

                foreach (var item in successStoryData.Items)
                {
                    bool isUnlocked = !string.IsNullOrEmpty(item.DateUnlockedStr);
                    DateTime? unlockDate = null;

                    if (isUnlocked)
                    {
                        // Try to parse the date
                        if (DateTime.TryParse(item.DateUnlockedStr, out DateTime parsedDate))
                        {
                            unlockDate = parsedDate;
                        }
                    }

                    achievements.Add(new AchievementData
                    {
                        Name = item.Name,
                        Description = item.Description,
                        IsUnlocked = isUnlocked,
                        UnlockDate = unlockDate,
                        IconUrl = isUnlocked ? item.UrlUnlocked : item.UrlLocked,
                        UnlockedIconUrl = item.UrlUnlocked,
                        LockedIconUrl = item.UrlLocked,
                        HasDistinctLockedIcon = !string.IsNullOrEmpty(item.UrlLocked) &&
                            !string.Equals(item.UrlLocked, item.UrlUnlocked, StringComparison.OrdinalIgnoreCase)
                    });
                }

                log($"Successfully parsed {achievements.Count} achievements from SuccessStory");
            }
            catch (Exception ex)
            {
                log($"Error parsing SuccessStory file: {ex.Message}", "ERROR");
            }

            return achievements;
        }

        #region Settings
        public override ISettings GetSettings(bool firstRunSettings)
        {
            if (settings == null)
            {
                settings = new OverlaySettings(this);
            }

            return settings;
        }

        public override System.Windows.Controls.UserControl GetSettingsView(bool firstRunSettings)
        {
            return new OverlaySettingsView();
        }

        // You might also want to add a public property to easily access settings
        public OverlaySettings Settings
        {
            get
            {
                if (settings == null)
                {
                    settings = (OverlaySettings)GetSettings(false);
                }

                return settings;
            }
        }
        #endregion

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;

        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(ProcessAccessFlags processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
            ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

        [Flags]
        private enum ProcessAccessFlags : uint
        {
            QueryInformation = 0x00000400
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2_0;
            public IntPtr Reserved2_1;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

    }

    public class AchievementData
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public bool IsUnlocked { get; set; }
        public DateTime? UnlockDate { get; set; }
        public string IconUrl { get; set; }

        // Extra details used by the full achievement list (0.4.5.3). All optional: SuccessStory
        // only fills the icon URLs, Playnite Achievements fills everything it has.
        public string UnlockedIconUrl { get; set; }
        public string LockedIconUrl { get; set; }
        /// <summary>True when the provider has a real "locked" icon different from the unlocked one.</summary>
        public bool HasDistinctLockedIcon { get; set; }
        /// <summary>Hidden/secret achievement (description is concealed in the list until unlocked or revealed).</summary>
        public bool IsHidden { get; set; }
        /// <summary>Global unlock percentage (0-100) if known.</summary>
        public double? GlobalPercentUnlocked { get; set; }
        /// <summary>Provider rarity bucket, e.g. Common / Uncommon / Rare / UltraRare.</summary>
        public string Rarity { get; set; }
        public int? ProgressNum { get; set; }
        public int? ProgressDenom { get; set; }
    }

    // Update your GameOverlayData class to include achievements
    public class GameOverlayData
    {
        public string GameName { get; set; }
        public int ProcessId { get; set; }
        public DateTime GameStartTime { get; set; }
        public TimeSpan Playtime { get; set; }
        public string CoverImagePath { get; set; }
        public List<AchievementData> Achievements { get; set; }
    }

    // Define classes that match the JSON structure
    public class SuccessStoryData
    {
        [JsonPropertyName("Items")]
        public List<SuccessStoryAchievement> Items { get; set; }

        [JsonPropertyName("Name")]
        public string Name { get; set; }
    }

    public class SuccessStoryAchievement
    {
        [JsonPropertyName("Name")]
        public string Name { get; set; }

        [JsonPropertyName("Description")]
        public string Description { get; set; }

        [JsonPropertyName("DateUnlocked")]
        public string DateUnlockedStr { get; set; }

        [JsonPropertyName("UrlUnlocked")]
        public string UrlUnlocked { get; set; }

        [JsonPropertyName("UrlLocked")]
        public string UrlLocked { get; set; }
    }

    public class OverlaySettings : ObservableObject, ISettings
    {
        private readonly PlayniteGameOverlay plugin;

        // Using properties with setters that notify of changes
        private ControllerShortcut _controllerShortcut = ControllerShortcut.StartBack;
        private CloseBehavior _closeBehavior = CloseBehavior.CloseAndEnd;
        private bool _debugMode = false;
        private AspectRatio _aspectRatio = AspectRatio.Portrait;

        // Toggle properties for showing/hiding buttons
        private bool _showRecordGameplay = true;
        private bool _showRecordRecent = true;
        private bool _showStreaming = true;
        private bool _showPerformanceOverlay = true;
        private bool _showScreenshotGallery = true;
        private bool _showWebBrowser = true;
        private bool _showBattery = true;
        private bool _showScreenshot = true;
        private bool _showInstantReplay = true;

        // Overlay views (achievement list now, screenshot gallery later): pause the game while open
        private bool _suspendGameInOverlayViews = true;

        // Shortcut and path properties.
        // Defaults are the NVIDIA App's standard keys. They are only used by a fresh install:
        // a saved config keeps its own values, including empty ones.
        private string _recordGameplayShortcut = "%{F9}";          // Alt+F9: start/stop recording
        private string _recordRecentShortcut = "%{F10}";           // Alt+F10: save Instant Replay
        private string _streamingShortcut = "";
        private string _performanceOverlayShortcut = "%r";         // Alt+R: performance overlay
        private string _screenshotGalleryPath = "";
        private string _webBrowserPath = "";
        private string _screenshotShortcut = "%{F1}";              // Alt+F1: screenshot
        private string _instantReplayShortcut = "+%{F10}";         // Alt+Shift+F10: Instant Replay on/off

        public ControllerShortcut ControllerShortcut
        {
            get => _controllerShortcut;
            set => SetValue(ref _controllerShortcut, value);
        }

        public CloseBehavior CloseBehavior
        {
            get => _closeBehavior;
            set => SetValue(ref _closeBehavior, value);
        }

        public bool DebugMode
        {
            get => _debugMode;
            set => SetValue(ref _debugMode, value);
        }

        public AspectRatio AspectRatio
        {
            get => _aspectRatio;
            set => SetValue(ref _aspectRatio, value);
        }

        // Properties for toggle options
        public bool ShowRecordGameplay
        {
            get => _showRecordGameplay;
            set => SetValue(ref _showRecordGameplay, value);
        }

        public bool ShowRecordRecent
        {
            get => _showRecordRecent;
            set => SetValue(ref _showRecordRecent, value);
        }

        public bool ShowStreaming
        {
            get => _showStreaming;
            set => SetValue(ref _showStreaming, value);
        }

        public bool ShowPerformanceOverlay
        {
            get => _showPerformanceOverlay;
            set => SetValue(ref _showPerformanceOverlay, value);
        }

        public bool ShowScreenshotGallery
        {
            get => _showScreenshotGallery;
            set => SetValue(ref _showScreenshotGallery, value);
        }

        public bool ShowWebBrowser
        {
            get => _showWebBrowser;
            set => SetValue(ref _showWebBrowser, value);
        }

        public bool ShowBattery
        {
            get => _showBattery;
            set => SetValue(ref _showBattery, value);
        }

        public bool ShowScreenshot
        {
            get => _showScreenshot;
            set => SetValue(ref _showScreenshot, value);
        }

        public bool ShowInstantReplay
        {
            get => _showInstantReplay;
            set => SetValue(ref _showInstantReplay, value);
        }

        // Properties for shortcuts and paths
        public string RecordGameplayShortcut
        {
            get => _recordGameplayShortcut;
            set => SetValue(ref _recordGameplayShortcut, value);
        }

        public string RecordRecentShortcut
        {
            get => _recordRecentShortcut;
            set => SetValue(ref _recordRecentShortcut, value);
        }

        public string StreamingShortcut
        {
            get => _streamingShortcut;
            set => SetValue(ref _streamingShortcut, value);
        }

        public string PerformanceOverlayShortcut
        {
            get => _performanceOverlayShortcut;
            set => SetValue(ref _performanceOverlayShortcut, value);
        }

        public string ScreenshotGalleryPath
        {
            get => _screenshotGalleryPath;
            set => SetValue(ref _screenshotGalleryPath, value);
        }

        public string WebBrowserPath
        {
            get => _webBrowserPath;
            set => SetValue(ref _webBrowserPath, value);
        }

        public string ScreenshotShortcut
        {
            get => _screenshotShortcut;
            set => SetValue(ref _screenshotShortcut, value);
        }

        public string InstantReplayShortcut
        {
            get => _instantReplayShortcut;
            set => SetValue(ref _instantReplayShortcut, value);
        }

        /// <summary>
        /// Suspend (freeze) the running game's process(es) while a full-screen overlay view such as the
        /// achievement list is open; they are resumed when the view closes. Default ON.
        /// </summary>
        public bool SuspendGameInOverlayViews
        {
            get => _suspendGameInOverlayViews;
            set => SetValue(ref _suspendGameInOverlayViews, value);
        }

        // Backup values for cancel operation
        private ControllerShortcut _controllerShortcutBackup;
        private CloseBehavior _closeBehaviorBackup;
        private bool _debugModeBackup;
        private bool _showRecordGameplayBackup;
        private bool _showRecordRecentBackup;
        private bool _showStreamingBackup;
        private bool _showPerformanceOverlayBackup;
        private bool _showScreenshotGalleryBackup;
        private bool _showWebBrowserBackup;
        private bool _showBatteryBackup;
        private string _recordGameplayShortcutBackup;
        private string _recordRecentShortcutBackup;
        private string _streamingShortcutBackup;
        private string _performanceOverlayShortcutBackup;
        private string _screenshotGalleryPathBackup;
        private string _webBrowserPathBackup;
        private bool _showScreenshotBackup;
        private bool _showInstantReplayBackup;
        private string _screenshotShortcutBackup;
        private string _instantReplayShortcutBackup;
        private bool _suspendGameInOverlayViewsBackup;

        // Parameterless constructor needed for serialization
        public OverlaySettings()
        {
        }

        // Main constructor that loads saved settings
        public OverlaySettings(PlayniteGameOverlay plugin)
        {
            this.plugin = plugin;

            // Load saved settings
            var savedSettings = plugin.LoadPluginSettings<OverlaySettings>();
            if (savedSettings != null)
            {
                if (savedSettings.ControllerShortcut != null)
                    ControllerShortcut = savedSettings.ControllerShortcut;
                if (savedSettings.CloseBehavior != null)
                    CloseBehavior = savedSettings.CloseBehavior;
                if (savedSettings.DebugMode != null)
                    DebugMode = savedSettings.DebugMode;
                if (savedSettings.AspectRatio != null)
                    AspectRatio = savedSettings.AspectRatio;

                // Load toggle settings
                if (savedSettings.ShowRecordGameplay != null && savedSettings.RecordGameplayShortcut != null)
                {
                    ShowRecordGameplay = savedSettings.ShowRecordGameplay;
                    RecordGameplayShortcut = savedSettings.RecordGameplayShortcut;
                }
                else
                {
                    ShowRecordGameplay = false;
                }
                if (savedSettings.ShowRecordRecent != null && savedSettings.RecordRecentShortcut != null)
                {
                    ShowRecordRecent = savedSettings.ShowRecordRecent;
                    RecordRecentShortcut = savedSettings.RecordRecentShortcut;
                }
                else
                {
                    ShowRecordRecent = false;
                }
                if (savedSettings.ShowStreaming != null && savedSettings.StreamingShortcut != null)
                {
                    ShowStreaming = savedSettings.ShowStreaming;
                    StreamingShortcut = savedSettings.StreamingShortcut;
                }
                else
                {
                    ShowStreaming = false;
                }
                if (savedSettings.ShowPerformanceOverlay != null && savedSettings.PerformanceOverlayShortcut != null)
                {
                    ShowPerformanceOverlay = savedSettings.ShowPerformanceOverlay;
                    PerformanceOverlayShortcut = savedSettings.PerformanceOverlayShortcut;
                }
                else
                {
                    ShowPerformanceOverlay = false;
                }
                if (savedSettings.ShowScreenshotGallery != null && savedSettings.ScreenshotGalleryPath != null)
                {
                    ShowScreenshotGallery = savedSettings.ShowScreenshotGallery;
                    ScreenshotGalleryPath = savedSettings.ScreenshotGalleryPath;
                }
                else
                {
                    ShowScreenshotGallery = false;
                }
                if (savedSettings.ShowWebBrowser != null && savedSettings.WebBrowserPath != null)
                {
                    ShowWebBrowser = savedSettings.ShowWebBrowser;
                    WebBrowserPath = savedSettings.WebBrowserPath;
                }
                else
                {
                    ShowWebBrowser = false;
                }
                if (savedSettings.ShowScreenshot != null && savedSettings.ScreenshotShortcut != null)
                {
                    ShowScreenshot = savedSettings.ShowScreenshot;
                    ScreenshotShortcut = savedSettings.ScreenshotShortcut;
                }
                else
                {
                    ShowScreenshot = false;
                }
                if (savedSettings.ShowInstantReplay != null && savedSettings.InstantReplayShortcut != null)
                {
                    ShowInstantReplay = savedSettings.ShowInstantReplay;
                    InstantReplayShortcut = savedSettings.InstantReplayShortcut;
                }
                else
                {
                    ShowInstantReplay = false;
                }
                if (savedSettings.ShowBattery != null) ShowBattery = savedSettings.ShowBattery;
                // Missing in configs written by <= 0.4.5.2 -> stays at its default (true) from the
                // parameterless constructor used by the JSON deserializer.
                SuspendGameInOverlayViews = savedSettings.SuspendGameInOverlayViews;
            }
        }

        public void BeginEdit()
        {
            // Backup current values in case user cancels
            _controllerShortcutBackup = ControllerShortcut;
            _debugModeBackup = DebugMode;
            _closeBehaviorBackup = CloseBehavior;

            // Backup toggle values
            _showRecordGameplayBackup = ShowRecordGameplay;
            _showRecordRecentBackup = ShowRecordRecent;
            _showStreamingBackup = ShowStreaming;
            _showPerformanceOverlayBackup = ShowPerformanceOverlay;
            _showScreenshotGalleryBackup = ShowScreenshotGallery;
            _showWebBrowserBackup = ShowWebBrowser;
            _showBatteryBackup = ShowBattery;

            // Backup shortcut and path values
            _recordGameplayShortcutBackup = RecordGameplayShortcut;
            _recordRecentShortcutBackup = RecordRecentShortcut;
            _streamingShortcutBackup = StreamingShortcut;
            _performanceOverlayShortcutBackup = PerformanceOverlayShortcut;
            _screenshotGalleryPathBackup = ScreenshotGalleryPath;
            _webBrowserPathBackup = WebBrowserPath;
            _showScreenshotBackup = ShowScreenshot;
            _showInstantReplayBackup = ShowInstantReplay;
            _screenshotShortcutBackup = ScreenshotShortcut;
            _instantReplayShortcutBackup = InstantReplayShortcut;
            _suspendGameInOverlayViewsBackup = SuspendGameInOverlayViews;
        }

        public void CancelEdit()
        {
            // Restore from backup
            ControllerShortcut = _controllerShortcutBackup;
            DebugMode = _debugModeBackup;
            CloseBehavior = _closeBehaviorBackup;

            // Restore toggle backups
            ShowRecordGameplay = _showRecordGameplayBackup;
            ShowRecordRecent = _showRecordRecentBackup;
            ShowStreaming = _showStreamingBackup;
            ShowPerformanceOverlay = _showPerformanceOverlayBackup;
            ShowScreenshotGallery = _showScreenshotGalleryBackup;
            ShowWebBrowser = _showWebBrowserBackup;
            ShowBattery = _showBatteryBackup;

            // Restore shortcut and path backups
            RecordGameplayShortcut = _recordGameplayShortcutBackup;
            RecordRecentShortcut = _recordRecentShortcutBackup;
            StreamingShortcut = _streamingShortcutBackup;
            PerformanceOverlayShortcut = _performanceOverlayShortcutBackup;
            ScreenshotGalleryPath = _screenshotGalleryPathBackup;
            WebBrowserPath = _webBrowserPathBackup;
            ShowScreenshot = _showScreenshotBackup;
            ShowInstantReplay = _showInstantReplayBackup;
            ScreenshotShortcut = _screenshotShortcutBackup;
            InstantReplayShortcut = _instantReplayShortcutBackup;
            SuspendGameInOverlayViews = _suspendGameInOverlayViewsBackup;
        }

        public void EndEdit()
        {
            // Save settings to persistent storage
            plugin.SavePluginSettings(this);
            plugin.ReloadOverlay(this);
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            // Add any validation logic here if needed
            return true;
        }
    }

    public enum ControllerShortcut
    {
        [Description("Start + Back")]
        StartBack,

        [Description("Guide (Xbox) button")]
        Guide,

        [Description("Off")]
        None
    }


    public enum CloseBehavior
    {
        [Description("Ask the game to close")]
        CloseWindow,

        [Description("Force the game to quit")]
        EndTask,

        [Description("Ask, then force quit")]
        CloseAndEnd
    }

    public enum AspectRatio
    {
        Portrait,
        Landscape,
        Square
    }

    public class ControllerState
    {
        public bool ButtonA { get; set; }
        public bool ButtonB { get; set; }
        public bool ButtonX { get; set; }
        public bool ButtonY { get; set; }
        public bool ButtonStart { get; set; }
        public bool ButtonBack { get; set; }
        public bool ButtonLeftBumper { get; set; }
        public bool ButtonRightBumper { get; set; }
        public Direction LeftStick { get; set; }
        public Direction RightStick { get; set; }
        public bool ButtonDPadUp { get; set; }
        public bool ButtonDPadDown { get; set; }
        public bool ButtonDPadLeft { get; set; }
        public bool ButtonDPadRight { get; set; }
        public bool ButtonGuide { get; set; } // Xbox Guide button
        public bool ButtonLeftTrigger { get; set; }
        public bool ButtonRightTrigger { get; set; }

        public ControllerInput? Update(OnControllerButtonStateChangedArgs args)
        {
            if (args == null || args.Button == null)
            {
                return null;
            }
            var pressed = args.State == ControllerInputState.Pressed;
            switch (args.Button)
            {
                case ControllerInput.A:
                    ButtonA = pressed;
                    break;
                case ControllerInput.B:
                    ButtonB = pressed;
                    break;
                case ControllerInput.X:
                    ButtonX = pressed;
                    break;
                case ControllerInput.Y:
                    ButtonY = pressed;
                    break;
                case ControllerInput.Start:
                    ButtonStart = pressed;
                    break;
                case ControllerInput.Back:
                    ButtonBack = pressed;
                    break;
                case ControllerInput.LeftShoulder:
                    ButtonLeftBumper = pressed;
                    break;
                case ControllerInput.RightShoulder:
                    ButtonRightBumper = pressed;
                    break;
                case ControllerInput.TriggerLeft:
                    ButtonLeftTrigger = pressed;
                    break;
                case ControllerInput.TriggerRight:
                    ButtonRightTrigger = pressed;
                    break;
                case ControllerInput.DPadUp:
                    ButtonDPadUp = pressed;
                    break;
                case ControllerInput.DPadDown:
                    ButtonDPadDown = pressed;
                    break;
                case ControllerInput.DPadLeft:
                    ButtonDPadLeft = pressed;
                    break;
                case ControllerInput.DPadRight:
                    ButtonDPadRight = pressed;
                    break;
                case ControllerInput.Guide:
                    ButtonGuide = pressed; // Xbox Guide button
                    break;
                case ControllerInput.LeftStickRight:
                    LeftStick = pressed ? Direction.Right : Direction.None;
                    break;
                case ControllerInput.LeftStickLeft:
                    LeftStick = pressed ? Direction.Left : Direction.None;
                    break;
                case ControllerInput.LeftStickUp:
                    LeftStick = pressed ? Direction.Up : Direction.None;
                    break;
                case ControllerInput.LeftStickDown:
                    LeftStick = pressed ? Direction.Down : Direction.None;
                    break;
                case ControllerInput.RightStickRight:
                    RightStick = pressed ? Direction.Right : Direction.None;
                    break;
                case ControllerInput.RightStickLeft:
                    RightStick = pressed ? Direction.Left : Direction.None;
                    break;
                case ControllerInput.RightStickUp:
                    RightStick = pressed ? Direction.Up : Direction.None;
                    break;
                case ControllerInput.RightStickDown:
                    RightStick = pressed ? Direction.Down : Direction.None;
                    break;
            }

            if (pressed)
            {
                return args.Button;
            }
            else
            {
                // Return null if the button was released
                return null;
            }
        }
    }

    public enum Direction
    {
        Up,
        Down,
        Left,
        Right,
        None
    }
}