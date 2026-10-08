using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PlayniteGameOverlay
{
    public static class ButtonActions
    {
        /// <summary>
        /// Sends a shortcut. Plain text uses SendKeys (^ Ctrl, + Shift, % Alt).
        /// A {WIN} token adds the Windows key, which SendKeys cannot send, and the
        /// chord is sent with SendInput, for recorders whose shortcuts use the Windows key.
        /// </summary>
        public static void ExecuteKbdShortcut(string keystroke)
        {
            if (string.IsNullOrWhiteSpace(keystroke))
                return;

            bool ctrl, alt, shift;
            int virtualKey;
            if (TryParseWindowsChord(keystroke, out ctrl, out alt, out shift, out virtualKey))
            {
                SendWindowsChord(ctrl, alt, shift, (ushort)virtualKey);
                return;
            }

            if (keystroke.IndexOf("{WIN}", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Debug.WriteLine("Unrecognized {WIN} shortcut: " + keystroke);
                return;
            }

            SendKeys.SendWait(keystroke);
        }

        /// <summary>
        /// Parses a shortcut that includes {WIN} plus optional SendKeys modifiers and one key.
        /// Examples: {WIN}%r, {WIN}%g, {WIN}%{PRTSC}, {WIN}+%{F10}.
        /// </summary>
        public static bool TryParseWindowsChord(string keystroke, out bool ctrl, out bool alt, out bool shift, out int virtualKey)
        {
            ctrl = alt = shift = false;
            virtualKey = 0;
            if (string.IsNullOrWhiteSpace(keystroke))
                return false;
            if (keystroke.IndexOf("{WIN}", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            string rest = Regex.Replace(keystroke, @"\{WIN\}", "", RegexOptions.IgnoreCase).Trim();
            int i = 0;
            while (i < rest.Length)
            {
                char c = rest[i];
                if (c == '^') ctrl = true;
                else if (c == '%') alt = true;
                else if (c == '+') shift = true;
                else break;
                i++;
            }

            string key = rest.Substring(i);
            if (key.Length == 1)
            {
                char c = char.ToUpperInvariant(key[0]);
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                {
                    virtualKey = c;
                    return true;
                }
                return false;
            }

            if (key.Length >= 3 && key[0] == '{' && key[key.Length - 1] == '}')
            {
                string name = key.Substring(1, key.Length - 2).ToUpperInvariant();
                if (name == "PRTSC" || name == "PRTSCN" || name == "PRINTSCREEN")
                {
                    virtualKey = 0x2C;
                    return true;
                }
                if (name.Length >= 2 && name[0] == 'F')
                {
                    int fn;
                    if (int.TryParse(name.Substring(1), out fn) && fn >= 1 && fn <= 24)
                    {
                        virtualKey = 0x70 + fn - 1;
                        return true;
                    }
                }
            }
            return false;
        }

        public static string DefaultCapturesFolder()
        {
            // The NVIDIA App saves into Videos\<game name>, so the Videos folder itself is the default.
            return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        }

        /// <summary>Opens a captures folder. An empty path means the user's Videos folder.</summary>
        public static void OpenCapturesFolder(string path)
        {
            string folder = string.IsNullOrWhiteSpace(path)
                ? DefaultCapturesFolder()
                : path.Trim().Trim('"');
            try
            {
                if (!Directory.Exists(folder))
                    Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Could not create captures folder: " + ex.Message);
            }
            if (Directory.Exists(folder))
                FocusOrLaunch(folder);
        }

        /// <summary>Opens a browser. An empty path means the system default browser.</summary>
        public static void OpenWebBrowser(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                string exe = GetDefaultBrowserExecutable();
                if (!string.IsNullOrEmpty(exe))
                {
                    FocusOrLaunch(exe);
                    return;
                }
                Debug.WriteLine("Could not resolve the default browser.");
                return;
            }
            FocusOrLaunch(path.Trim().Trim('"'));
        }

        public static string GetDefaultBrowserExecutable()
        {
            try
            {
                using (var choice = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice"))
                {
                    var progId = choice == null ? null : choice.GetValue("ProgId") as string;
                    if (string.IsNullOrWhiteSpace(progId))
                        return null;
                    using (var commandKey = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command"))
                    {
                        var command = commandKey == null ? null : commandKey.GetValue(null) as string;
                        return ExtractExecutable(command);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Default browser lookup failed: " + ex.Message);
                return null;
            }
        }

        private static string ExtractExecutable(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return null;
            command = command.Trim();
            string path;
            if (command.StartsWith("\""))
            {
                int end = command.IndexOf('"', 1);
                if (end <= 1)
                    return null;
                path = command.Substring(1, end - 1);
            }
            else
            {
                int space = command.IndexOf(' ');
                path = space > 0 ? command.Substring(0, space) : command;
            }
            return File.Exists(path) ? path : null;
        }

        public static void FocusOrLaunch(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            path = path.Trim().Trim('"');

            // Folders (for example a captures folder) are opened in Explorer.
            if (Directory.Exists(path))
            {
                string folder = path.TrimEnd('\\');
                // Drive roots ("D:") need the trailing backslash and must not be quoted with it.
                string args = folder.EndsWith(":") ? folder + "\\" : "\"" + folder + "\"";
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = args,
                    UseShellExecute = true
                });
                return;
            }

            bool processFound = false;
            try
            {
                string fileName = Path.GetFileNameWithoutExtension(path);
                Process[] processes = Process.GetProcessesByName(fileName);
                if (processes.Length > 0)
                {
                    // Only treat a process as found when it has a main window.
                    // Some browsers keep windowless background processes running.
                    foreach (var process in processes)
                    {
                        try
                        {
                            IntPtr handle = process.MainWindowHandle;
                            if (handle != IntPtr.Zero)
                            {
                                ShowWindow(handle, SW_MAXIMIZE);
                                SetForegroundWindow(handle);
                                processFound = true;
                                break;
                            }
                        }
                        catch
                        {
                            continue;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error checking for an existing process: " + ex.Message);
            }

            if (!processFound)
            {
                Process.Start(path);
            }
        }

        private static void SendWindowsChord(bool ctrl, bool alt, bool shift, ushort virtualKey)
        {
            var down = new List<ushort>();
            down.Add(0x5B); // VK_LWIN
            if (ctrl) down.Add(0x11);
            if (alt) down.Add(0x12);
            if (shift) down.Add(0x10);
            down.Add(virtualKey);

            var inputs = new INPUT[down.Count * 2];
            for (int i = 0; i < down.Count; i++)
                inputs[i] = KeyInput(down[i], false);
            for (int i = 0; i < down.Count; i++)
                inputs[down.Count + i] = KeyInput(down[down.Count - 1 - i], true);

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        private static INPUT KeyInput(ushort virtualKey, bool up)
        {
            return new INPUT
            {
                type = 1,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = virtualKey,
                        wScan = 0,
                        dwFlags = up ? 2u : 0u,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        private const int SW_MAXIMIZE = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }
    }
}
