using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Playnite.SDK;

namespace PlayniteGameOverlay
{
    /// <summary>
    /// Freezes / unfreezes game processes while a full-screen overlay view (achievement list, later the
    /// screenshot gallery) is open, using ntdll's NtSuspendProcess / NtResumeProcess.
    ///
    /// Safety rules:
    ///  - never touches Playnite itself, processes in another session (services), processes whose image
    ///    lives under the Windows directory or Playnite's install directory, or a deny-list of shell /
    ///    system / launcher / overlay processes (explorer, dwm, csrss, Steam, Discord, OBS, ...);
    ///  - a process whose image path cannot be read is skipped (no blind suspends);
    ///  - every process suspended here is remembered together with an open handle (so a recycled PID
    ///    can never be resumed by mistake) and resumed exactly once by <see cref="ResumeAll"/>;
    ///  - ResumeAll is idempotent and is called from many places (view closed, overlay hidden/closed,
    ///    game stopped, watchdog, plugin Dispose, Playnite shutdown, unhandled exceptions, finalizer).
    /// </summary>
    public sealed class GameProcessSuspender : IDisposable
    {
        private readonly ILogger logger;
        private readonly object sync = new object();
        private readonly List<SuspendedProcess> suspended = new List<SuspendedProcess>();
        private static readonly int selfPid = Process.GetCurrentProcess().Id;

        private sealed class SuspendedProcess
        {
            public int Pid;
            public string Name;
            public IntPtr Handle;
        }

        private static readonly HashSet<string> DeniedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Windows core / shell
            "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
            "svchost", "dwm", "explorer", "sihost", "fontdrvhost", "ctfmon", "taskhostw", "RuntimeBroker",
            "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp", "SearchUI",
            "TextInputHost", "ApplicationFrameHost", "LockApp", "conhost", "audiodg", "Taskmgr",
            "SecurityHealthSystray", "SecurityHealthService", "MsMpEng", "dllhost", "WmiPrvSE", "spoolsv",
            "SystemSettings", "UserOOBEBroker", "smartscreen", "consent", "LogonUI", "WindowsTerminal",
            "OpenConsole", "cmd", "powershell", "pwsh",
            // Game launchers / clients
            "steam", "steamwebhelper", "steamservice", "GameOverlayUI", "EpicGamesLauncher",
            "EpicWebHelper", "GalaxyClient", "GalaxyClientService", "GOG Galaxy Notifications Renderer",
            "EADesktop", "EABackgroundService", "Origin", "upc", "UbisoftConnect", "Battle.net",
            "XboxPcApp", "GamingServices", "GamingServicesNet",
            // Overlays, capture, chat, tools
            "Discord", "obs64", "obs32", "obs", "nvcontainer", "NVIDIA Share", "NVIDIA Overlay",
            "NVIDIA Web Helper", "nvsphelper64", "RTSS", "RTSSHooksLoader64", "MSIAfterburner",
            "GameBar", "GameBarFTServer", "GameBarPresenceWriter", "XboxGameBarWidgets",
            "AMDRSServ", "RadeonSoftware", "AutoHotkey", "AutoHotkey64", "AutoHotkeyU64",
            "DS4Windows", "SteamController", "Rainmeter", "MicrosoftEdge",
        };

        /// <summary>Products that are never suspended, matched on the executable's ProductName (browser and its WebView2 runtime).</summary>
        private static readonly string[] DeniedProductPrefixes = { "Microsoft Edge" };

        public GameProcessSuspender(ILogger logger)
        {
            this.logger = logger;
        }

        /// <summary>True while at least one process is suspended by this instance.</summary>
        public bool HasSuspendedProcesses
        {
            get { lock (sync) { return suspended.Count > 0; } }
        }

        /// <summary>
        /// Suspends the given processes (skipping unsafe ones and ones already suspended by us).
        /// Never throws; returns how many processes were actually suspended by this call.
        /// </summary>
        public int Suspend(IEnumerable<int> pids, string reason, string playniteDir)
        {
            int count = 0;
            if (pids == null) return 0;
            lock (sync)
            {
                foreach (var pid in pids.Distinct())
                {
                    try
                    {
                        if (suspended.Any(s => s.Pid == pid))
                            continue;

                        string name, path, why;
                        if (!IsSafeToSuspend(pid, playniteDir, out name, out path, out why))
                        {
                            logger.Info($"[Overlay suspend] Skipping PID {pid} ({name ?? "?"}): {why}");
                            continue;
                        }

                        var handle = OpenProcess(PROCESS_SUSPEND_RESUME | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                        if (handle == IntPtr.Zero)
                        {
                            logger.Warn($"[Overlay suspend] Could not open {name} (PID {pid}) for suspend: Win32 error {Marshal.GetLastWin32Error()}");
                            continue;
                        }

                        int status = NtSuspendProcess(handle);
                        if (status != 0)
                        {
                            logger.Warn($"[Overlay suspend] NtSuspendProcess failed for {name} (PID {pid}): NTSTATUS 0x{status:X8}");
                            CloseHandle(handle);
                            continue;
                        }

                        suspended.Add(new SuspendedProcess { Pid = pid, Name = name, Handle = handle });
                        count++;
                        logger.Info($"[Overlay suspend] Suspended {name} (PID {pid}, {path}) - {reason}");
                    }
                    catch (Exception ex)
                    {
                        logger.Warn($"[Overlay suspend] Error suspending PID {pid}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            return count;
        }

        /// <summary>Resumes everything suspended by this instance. Idempotent, never throws.</summary>
        public void ResumeAll(string reason)
        {
            List<SuspendedProcess> toResume;
            lock (sync)
            {
                if (suspended.Count == 0) return;
                toResume = new List<SuspendedProcess>(suspended);
                suspended.Clear();
            }

            foreach (var s in toResume)
            {
                try
                {
                    int status = NtResumeProcess(s.Handle);
                    if (status == 0)
                        SafeLog(() => logger.Info($"[Overlay suspend] Resumed {s.Name} (PID {s.Pid}) - {reason}"));
                    else
                        SafeLog(() => logger.Warn($"[Overlay suspend] NtResumeProcess failed for {s.Name} (PID {s.Pid}): NTSTATUS 0x{status:X8} - {reason}"));
                }
                catch (Exception ex)
                {
                    SafeLog(() => logger.Warn($"[Overlay suspend] Error resuming {s.Name} (PID {s.Pid}): {ex.Message}"));
                }
                finally
                {
                    try { CloseHandle(s.Handle); } catch { }
                }
            }
        }

        public void Dispose()
        {
            ResumeAll("dispose");
            GC.SuppressFinalize(this);
        }

        ~GameProcessSuspender()
        {
            // Last-resort safety net: never leave a game frozen. No logging from the finalizer thread.
            lock (sync)
            {
                foreach (var s in suspended)
                {
                    try { NtResumeProcess(s.Handle); CloseHandle(s.Handle); } catch { }
                }
                suspended.Clear();
            }
        }

        private static void SafeLog(Action a)
        {
            try { a(); } catch { }
        }

        /// <summary>Checks the safety rules described on the class.</summary>
        public static bool IsSafeToSuspend(int pid, string playniteDir, out string name, out string path, out string why)
        {
            name = null;
            path = null;
            why = null;

            if (pid <= 4) { why = "system PID"; return false; }
            if (pid == selfPid) { why = "this is Playnite itself"; return false; }

            Process p;
            try { p = Process.GetProcessById(pid); }
            catch { why = "process not running"; return false; }

            using (p)
            {
                try { name = p.ProcessName; } catch { why = "cannot read process name"; return false; }
                try
                {
                    if (p.HasExited) { why = "process has exited"; return false; }
                }
                catch { /* access denied on HasExited for protected processes; the checks below still apply */ }

                if (DeniedNames.Contains(name) || name.StartsWith("Playnite", StringComparison.OrdinalIgnoreCase))
                {
                    why = "system/shell/launcher process (deny-list)";
                    return false;
                }

                try
                {
                    if (p.SessionId != Process.GetCurrentProcess().SessionId) { why = "different session (service)"; return false; }
                }
                catch { why = "cannot read session"; return false; }
            }

            path = TryGetProcessPath(pid);
            if (string.IsNullOrEmpty(path)) { why = "cannot read executable path"; return false; }

            var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (IsUnderDirectory(path, windowsDir)) { why = "executable is under the Windows directory"; return false; }
            if (!string.IsNullOrEmpty(playniteDir) && IsUnderDirectory(path, playniteDir)) { why = "executable is under the Playnite directory"; return false; }

            string product = null;
            try { product = FileVersionInfo.GetVersionInfo(path).ProductName; } catch { }
            if (!string.IsNullOrEmpty(product) && DeniedProductPrefixes.Any(d => product.StartsWith(d, StringComparison.OrdinalIgnoreCase)))
            {
                why = "browser process (" + product + ")";
                return false;
            }

            return true;
        }

        /// <summary>Full image path via QueryFullProcessImageName (works for 64-bit targets from the x86 Playnite process).</summary>
        public static string TryGetProcessPath(int pid)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) return null;
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (h != IntPtr.Zero) CloseHandle(h);
            }
        }

        public static bool IsUnderDirectory(string path, string directory)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory)) return false;
            try
            {
                var full = Path.GetFullPath(path);
                var dir = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;
                return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static bool IsAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    try { return !p.HasExited; } catch { return true; }
                }
            }
            catch
            {
                return false;
            }
        }

        private const uint PROCESS_SUSPEND_RESUME = 0x0800;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

        [DllImport("ntdll.dll")]
        private static extern int NtSuspendProcess(IntPtr processHandle);

        [DllImport("ntdll.dll")]
        private static extern int NtResumeProcess(IntPtr processHandle);
    }
}
