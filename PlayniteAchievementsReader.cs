using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace PlayniteGameOverlay
{
    /// <summary>
    /// Read-only access to the "Playnite Achievements" extension (Id: PlayniteAchievements,
    /// plugin GUID e6aad2c9-6e06-4d8d-ac55-ac3b252b5f7b) cache database:
    /// ExtensionsData\e6aad2c9-...\achievement_cache.db (SQLite, WAL mode, schema v18).
    /// Uses Playnite's own bundled sqlite3.x86.dll via P/Invoke; never writes to the database.
    /// </summary>
    public static class PlayniteAchievementsReader
    {
        public static readonly Guid PluginId = Guid.Parse("e6aad2c9-6e06-4d8d-ac55-ac3b252b5f7b");
        public const string DatabaseFileName = "achievement_cache.db";

        // Current-user achievements for one Playnite game (all providers linked to it).
        // Columns 0-9 are the 0.4.5.2 set; 10-12 (Rarity, ProgressNum, ProgressDenom) were added in
        // 0.4.5.3 for the full achievement list and are optional (older schemas fall back to 0-9).
        private const string BaseColumns = @"
SELECT ad.DisplayName, ad.ApiName, ad.Description, ad.UnlockedIconPath, ad.LockedIconPath,
       ad.Hidden, ad.GlobalPercentUnlocked, IFNULL(ua.Unlocked, 0), ua.UnlockTimeUtc, g.ProviderKey";

        private const string ExtraColumns = @",
       ad.Rarity, ua.ProgressNum, ua.ProgressDenom";

        private const string FromClause = @"
FROM Games g
JOIN UserGameProgress ugp ON ugp.GameId = g.Id
JOIN Users u ON u.Id = ugp.UserId AND u.IsCurrentUser = 1
JOIN AchievementDefinitions ad ON ad.GameId = g.Id
LEFT JOIN UserAchievements ua ON ua.UserGameProgressId = ugp.Id AND ua.AchievementDefinitionId = ad.Id";

        // Honors the user's per-achievement exclusions (AchievementOverrides, schema v18).
        private const string OverridesClause = @"
LEFT JOIN AchievementOverrides ao ON ao.PlayniteGameId = g.PlayniteGameId AND ao.ApiName = ad.ApiName
WHERE g.PlayniteGameId = ?1 COLLATE NOCASE
  AND IFNULL(ao.IsFiltered, 0) = 0 AND IFNULL(ao.IsSummaryFiltered, 0) = 0
ORDER BY g.Id, ad.Id;";

        // Fallback for older/newer schemas without the overrides table.
        private const string PlainClause = @"
WHERE g.PlayniteGameId = ?1 COLLATE NOCASE
ORDER BY g.Id, ad.Id;";

        // Tried in order until one prepares successfully.
        private static readonly string[] Queries =
        {
            BaseColumns + ExtraColumns + FromClause + OverridesClause,
            BaseColumns + ExtraColumns + FromClause + PlainClause,
            BaseColumns + FromClause + OverridesClause,
            BaseColumns + FromClause + PlainClause,
        };

        private static bool nativePreloaded;

        /// <summary>Optionally preload sqlite3.x86.dll from a known directory (e.g. Playnite's install dir).</summary>
        public static void PreloadNative(string directory, Action<string> log = null)
        {
            if (nativePreloaded || string.IsNullOrEmpty(directory)) return;
            try
            {
                var path = Path.Combine(directory, Native.LibName);
                if (File.Exists(path) && LoadLibrary(path) != IntPtr.Zero)
                    nativePreloaded = true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"PlayniteAchievements: could not preload {Native.LibName}: {ex.Message}");
            }
        }

        public static string GetDatabasePath(string extensionsDataPath)
        {
            return Path.Combine(extensionsDataPath, PluginId.ToString(), DatabaseFileName);
        }

        /// <summary>
        /// Returns the game's achievements, or null if the game is not tracked / anything fails
        /// (failures are reported through <paramref name="log"/>, never thrown).
        /// </summary>
        public static List<AchievementData> GetAchievements(string extensionsDataPath, Guid gameId, Action<string> log = null)
        {
            try
            {
                if (IntPtr.Size != 4)
                {
                    log?.Invoke("PlayniteAchievements: 64-bit process is not supported (sqlite3.x86.dll).");
                    return null;
                }

                var dataDir = Path.Combine(extensionsDataPath, PluginId.ToString());
                var dbPath = Path.Combine(dataDir, DatabaseFileName);
                if (!File.Exists(dbPath))
                {
                    log?.Invoke($"PlayniteAchievements: database not found at {dbPath}");
                    return null;
                }

                var result = new List<AchievementData>();
                IntPtr db = IntPtr.Zero, stmt = IntPtr.Zero;
                try
                {
                    int rc = Native.sqlite3_open_v2(Utf8z(dbPath), out db, SQLITE_OPEN_READONLY, IntPtr.Zero);
                    if (rc != SQLITE_OK) throw new InvalidOperationException($"open failed ({rc}): {ErrMsg(db)}");
                    Native.sqlite3_busy_timeout(db, 3000);

                    rc = SQLITE_ERROR;
                    for (int qi = 0; qi < Queries.Length; qi++)
                    {
                        rc = Native.sqlite3_prepare16_v2(db, Queries[qi], -1, out stmt, IntPtr.Zero);
                        if (rc == SQLITE_OK) break;
                        log?.Invoke($"PlayniteAchievements: query variant {qi} failed ({ErrMsg(db)}), trying a simpler one.");
                        if (stmt != IntPtr.Zero) { Native.sqlite3_finalize(stmt); stmt = IntPtr.Zero; }
                    }
                    if (rc != SQLITE_OK) throw new InvalidOperationException($"prepare failed ({rc}): {ErrMsg(db)}");
                    bool hasExtra = Native.sqlite3_column_count(stmt) >= 13;

                    rc = Native.sqlite3_bind_text16(stmt, 1, gameId.ToString(), -1, SQLITE_TRANSIENT);
                    if (rc != SQLITE_OK) throw new InvalidOperationException($"bind failed ({rc}): {ErrMsg(db)}");

                    while ((rc = Native.sqlite3_step(stmt)) == SQLITE_ROW)
                    {
                        var displayName = Text(stmt, 0);
                        var apiName = Text(stmt, 1);
                        var description = Text(stmt, 2);
                        var unlockedIcon = Text(stmt, 3);
                        var lockedIcon = Text(stmt, 4);
                        bool hidden = Native.sqlite3_column_int64(stmt, 5) != 0;
                        double? globalPercent = Native.sqlite3_column_type(stmt, 6) == SQLITE_NULL
                            ? (double?)null : Native.sqlite3_column_double(stmt, 6);
                        bool unlocked = Native.sqlite3_column_int64(stmt, 7) != 0;
                        var unlockTime = ParseUtc(Text(stmt, 8));
                        string rarity = hasExtra ? Text(stmt, 10) : null;
                        int? progressNum = hasExtra ? NullableInt(stmt, 11) : null;
                        int? progressDenom = hasExtra ? NullableInt(stmt, 12) : null;

                        var unlockedIconPath = ResolveIcon(dataDir, unlockedIcon);
                        var lockedIconPath = ResolveIcon(dataDir, lockedIcon);

                        result.Add(new AchievementData
                        {
                            Name = string.IsNullOrWhiteSpace(displayName) ? apiName : displayName,
                            Description = description,
                            IsUnlocked = unlocked,
                            UnlockDate = unlocked ? unlockTime : null,
                            IconUrl = (unlocked ? unlockedIconPath : (lockedIconPath ?? unlockedIconPath)) ?? unlockedIconPath,
                            UnlockedIconUrl = unlockedIconPath,
                            LockedIconUrl = lockedIconPath,
                            // Playnite Achievements currently stores the same file for both (checked on the
                            // user's db: LockedIconPath == UnlockedIconPath for every row), so locked rows
                            // are greyed out by the overlay unless a genuinely different locked icon exists.
                            HasDistinctLockedIcon = lockedIconPath != null && unlockedIconPath != null &&
                                !string.Equals(lockedIconPath, unlockedIconPath, StringComparison.OrdinalIgnoreCase),
                            IsHidden = hidden,
                            GlobalPercentUnlocked = globalPercent,
                            Rarity = rarity,
                            ProgressNum = progressNum,
                            ProgressDenom = progressDenom
                        });
                    }

                    if (rc != SQLITE_DONE) throw new InvalidOperationException($"step failed ({rc}): {ErrMsg(db)}");
                }
                finally
                {
                    if (stmt != IntPtr.Zero) Native.sqlite3_finalize(stmt);
                    if (db != IntPtr.Zero) Native.sqlite3_close(db);
                }

                return result.Count > 0 ? result : null;
            }
            catch (Exception ex)
            {
                log?.Invoke($"PlayniteAchievements: failed to read achievements for {gameId}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Turns a stored icon reference into something a WPF Image can load (absolute file path or http(s) URL).</summary>
        public static string ResolveIcon(string dataDir, string stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return null;
            stored = stored.Trim();

            if (stored.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                stored.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return stored;

            if (stored.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                try { stored = new Uri(stored).LocalPath; } catch { return null; }
            }

            try
            {
                var path = Path.IsPathRooted(stored)
                    ? stored
                    : Path.Combine(dataDir, stored.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(path) ? path : null;
            }
            catch
            {
                return null;
            }
        }

        private static DateTime? ParseUtc(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            DateTime dt;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dt))
                return dt.ToLocalTime();
            long ticks;
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks))
            {
                // unix seconds
                if (ticks > 0 && ticks < 100000000000L)
                    return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(ticks).ToLocalTime();
            }
            return null;
        }

        private static string Text(IntPtr stmt, int col)
        {
            if (Native.sqlite3_column_type(stmt, col) == SQLITE_NULL) return null;
            var p = Native.sqlite3_column_text16(stmt, col);
            return p == IntPtr.Zero ? null : Marshal.PtrToStringUni(p);
        }

        private static int? NullableInt(IntPtr stmt, int col)
        {
            if (Native.sqlite3_column_type(stmt, col) == SQLITE_NULL) return null;
            long v = Native.sqlite3_column_int64(stmt, col);
            if (v > int.MaxValue) return int.MaxValue;
            if (v < int.MinValue) return int.MinValue;
            return (int)v;
        }

        private static string ErrMsg(IntPtr db)
        {
            try
            {
                if (db == IntPtr.Zero) return "(no handle)";
                var p = Native.sqlite3_errmsg16(db);
                return p == IntPtr.Zero ? "" : Marshal.PtrToStringUni(p);
            }
            catch { return ""; }
        }

        private static byte[] Utf8z(string s)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(s);
            var z = new byte[bytes.Length + 1];
            Buffer.BlockCopy(bytes, 0, z, 0, bytes.Length);
            return z;
        }

        private const int SQLITE_OK = 0;
        private const int SQLITE_ERROR = 1;
        private const int SQLITE_ROW = 100;
        private const int SQLITE_DONE = 101;
        private const int SQLITE_NULL = 5;
        private const int SQLITE_OPEN_READONLY = 0x00000001;
        private static readonly IntPtr SQLITE_TRANSIENT = new IntPtr(-1);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        private static class Native
        {
            public const string LibName = "sqlite3.x86.dll";

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int sqlite3_busy_timeout(IntPtr db, int ms);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
            public static extern int sqlite3_prepare16_v2(IntPtr db, [MarshalAs(UnmanagedType.LPWStr)] string sql, int nBytes, out IntPtr stmt, IntPtr tail);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
            public static extern int sqlite3_bind_text16(IntPtr stmt, int index, [MarshalAs(UnmanagedType.LPWStr)] string value, int nBytes, IntPtr destructor);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int sqlite3_step(IntPtr stmt);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int sqlite3_column_type(IntPtr stmt, int col);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr sqlite3_column_text16(IntPtr stmt, int col);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern long sqlite3_column_int64(IntPtr stmt, int col);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern double sqlite3_column_double(IntPtr stmt, int col);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int sqlite3_column_count(IntPtr stmt);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int sqlite3_finalize(IntPtr stmt);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int sqlite3_close(IntPtr db);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr sqlite3_errmsg16(IntPtr db);
        }
    }
}
