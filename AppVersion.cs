using System;
using System.Reflection;

namespace PlayniteGameOverlay
{
    /// <summary>
    /// Version shown in the overlay and in the settings page. Read from the assembly
    /// (AssemblyInformationalVersion, else AssemblyVersion), never hardcoded.
    /// </summary>
    public static class AppVersion
    {
        public const string ReleasesUrl = "https://github.com/aleksejsgx/PlayniteNextOverlay/releases";

        public static string Version { get; } = ReadVersion();

        /// <summary>Short label, e.g. "v1.0.4".</summary>
        public static string Display => "v" + Version;

        /// <summary>Settings page label, e.g. "Playnite Next Overlay v1.0.4".</summary>
        public static string SettingsText => "Playnite Next Overlay v" + Version;

        private static string ReadVersion()
        {
            try
            {
                var asm = typeof(AppVersion).Assembly;
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info))
                    return info.Split('+')[0].Trim();
                var v = asm.GetName().Version;
                return v != null ? v.ToString(3) : "?";
            }
            catch
            {
                return "?";
            }
        }
    }
}