using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Playnite.SDK;

namespace PlayniteGameOverlay
{
    public enum AchievementFilter
    {
        All,
        Unlocked,
        Locked
    }

    /// <summary>Pure (UI-free) rules of the achievement list, shared with the test harness.</summary>
    public static class AchievementListLogic
    {
        /// <summary>Unlocked first (most recent first, undated unlocks after dated ones), then locked in provider order.</summary>
        public static List<AchievementData> SortDefault(IEnumerable<AchievementData> source)
        {
            var indexed = (source ?? Enumerable.Empty<AchievementData>())
                .Where(a => a != null)
                .Select((a, i) => new { a, i })
                .ToList();

            var unlocked = indexed.Where(x => x.a.IsUnlocked)
                .OrderByDescending(x => x.a.UnlockDate.HasValue)
                .ThenByDescending(x => x.a.UnlockDate ?? DateTime.MinValue)
                .ThenBy(x => x.i);
            var locked = indexed.Where(x => !x.a.IsUnlocked)
                .OrderBy(x => x.i);

            return unlocked.Concat(locked).Select(x => x.a).ToList();
        }

        public static bool Matches(AchievementData a, AchievementFilter filter)
        {
            switch (filter)
            {
                case AchievementFilter.Unlocked: return a.IsUnlocked;
                case AchievementFilter.Locked: return !a.IsUnlocked;
                default: return true;
            }
        }

        public static AchievementFilter Cycle(AchievementFilter current, int direction)
        {
            int n = 3;
            int next = (((int)current + (direction >= 0 ? 1 : -1)) % n + n) % n;
            return (AchievementFilter)next;
        }

        /// <summary>Hidden (secret) achievements keep their description concealed until unlocked or revealed.</summary>
        public static bool IsConcealed(AchievementData a, bool revealHidden)
        {
            return a.IsHidden && !a.IsUnlocked && !revealHidden;
        }

        public const string ConcealedDescription = "Secret achievement. The description stays hidden until you unlock it, or press Y or H to reveal it.";

        public static string DisplayDescription(AchievementData a, bool revealHidden)
        {
            if (IsConcealed(a, revealHidden))
                return ConcealedDescription;
            return a.Description ?? string.Empty;
        }

        public static string FormatRarity(AchievementData a)
        {
            string bucket = FormatRarityBucket(a.Rarity);
            if (a.GlobalPercentUnlocked.HasValue)
            {
                var pct = a.GlobalPercentUnlocked.Value.ToString("0.#", CultureInfo.CurrentCulture) + "% of players";
                return string.IsNullOrEmpty(bucket) ? pct : pct + "  \u00B7  " + bucket;
            }
            return bucket ?? string.Empty;
        }

        public static string FormatRarityBucket(string rarity)
        {
            if (string.IsNullOrWhiteSpace(rarity)) return null;
            switch (rarity.Trim().ToLowerInvariant())
            {
                case "ultrarare": return "Ultra Rare";
                case "veryrare": return "Very Rare";
                default: return rarity.Trim();
            }
        }

        public static string RarityColor(AchievementData a)
        {
            switch ((a.Rarity ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "uncommon": return "#FF7FC97F";
                case "rare": return "#FF6FA8FF";
                case "ultrarare":
                case "veryrare": return "#FFE0B040";
                default: return "#FF9A9A9A";
            }
        }

        public static string FormatStatus(AchievementData a)
        {
            if (!a.IsUnlocked) return "Locked";
            if (!a.UnlockDate.HasValue) return "Unlocked";
            return "Unlocked " + a.UnlockDate.Value.ToString("g", CultureInfo.CurrentCulture);
        }

        public static string FormatProgress(AchievementData a)
        {
            if (a.IsUnlocked || !a.ProgressDenom.HasValue || a.ProgressDenom.Value <= 0) return null;
            return $"Progress {a.ProgressNum ?? 0}/{a.ProgressDenom.Value}";
        }

        public static int Percent(int unlocked, int total)
        {
            return total <= 0 ? 0 : (int)Math.Round(100.0 * unlocked / total, MidpointRounding.AwayFromZero);
        }

        /// <summary>Whether the overlay greys out the icon itself (locked and no real locked icon available).</summary>
        public static bool NeedsGreyscale(AchievementData a)
        {
            return !a.IsUnlocked && !a.HasDistinctLockedIcon;
        }
    }

    /// <summary>Small, memory-friendly icon loading (decoded at small size, file never kept locked).</summary>
    public static class AchievementIconLoader
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        public static ImageSource Load(string iconUrl, bool greyscale, int decodePixelWidth)
        {
            if (string.IsNullOrWhiteSpace(iconUrl))
                return null;
            try
            {
                Uri uri;
                if (iconUrl.StartsWith("/") && iconUrl.IndexOf(";component/", StringComparison.OrdinalIgnoreCase) > 0)
                    uri = new Uri("pack://application:,,," + iconUrl, UriKind.Absolute);
                else if (System.IO.Path.IsPathRooted(iconUrl) && !iconUrl.Contains("://"))
                    uri = new Uri(iconUrl, UriKind.Absolute);
                else if (!Uri.TryCreate(iconUrl, UriKind.Absolute, out uri))
                    return null;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = uri;
                bmp.DecodePixelWidth = decodePixelWidth;
                if (uri.IsFile)
                {
                    bmp.CacheOption = BitmapCacheOption.OnLoad;   // read fully now, release the file
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                }
                bmp.EndInit();

                if (!uri.IsFile)
                    return bmp;   // remote: WPF downloads asynchronously; no greyscale (opacity only)

                bmp.Freeze();
                if (!greyscale)
                    return bmp;

                var grey = new FormatConvertedBitmap(bmp, PixelFormats.Gray8, null, 0);
                grey.Freeze();
                return grey;
            }
            catch (Exception ex)
            {
                try { logger.Warn($"Could not load achievement icon '{iconUrl}': {ex.Message}"); } catch { }
                return null;
            }
        }
    }

    public class AchievementListItem : INotifyPropertyChanged
    {
        public const int IconDecodeSize = 64;

        private bool revealHidden;
        private bool iconLoaded;
        private ImageSource icon;

        public AchievementListItem(AchievementData data, bool revealHidden)
        {
            Data = data;
            this.revealHidden = revealHidden;
        }

        public AchievementData Data { get; }

        public string Name => Data.Name;
        public bool IsUnlocked => Data.IsUnlocked;
        public bool IsLocked => !Data.IsUnlocked;
        public bool IsHidden => Data.IsHidden;
        public bool IsConcealed => AchievementListLogic.IsConcealed(Data, revealHidden);
        public string DisplayDescription => AchievementListLogic.DisplayDescription(Data, revealHidden);
        public string RarityText => AchievementListLogic.FormatRarity(Data);
        public bool HasRarity => !string.IsNullOrEmpty(RarityText);
        public string RarityColor => AchievementListLogic.RarityColor(Data);
        public string StatusText => AchievementListLogic.FormatStatus(Data);
        public string ProgressText => AchievementListLogic.FormatProgress(Data);
        public bool HasProgress => ProgressText != null;

        /// <summary>Locked icons that the overlay had to grey out itself are also dimmed.</summary>
        public double IconOpacity => IsUnlocked ? 1.0 : (Data.HasDistinctLockedIcon ? 0.85 : 0.5);

        /// <summary>
        /// Loaded lazily the first time a (virtualized) row actually displays this item, decoded at
        /// <see cref="IconDecodeSize"/> px, greyscale for locked achievements without a real locked icon.
        /// </summary>
        public ImageSource Icon
        {
            get
            {
                if (!iconLoaded)
                {
                    iconLoaded = true;
                    icon = AchievementIconLoader.Load(Data.IconUrl, AchievementListLogic.NeedsGreyscale(Data), IconDecodeSize);
                }
                return icon;
            }
        }

        internal void SetRevealHidden(bool reveal)
        {
            if (revealHidden == reveal) return;
            revealHidden = reveal;
            if (Data.IsHidden)
            {
                OnPropertyChanged(nameof(IsConcealed));
                OnPropertyChanged(nameof(DisplayDescription));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public class AchievementListViewModel : INotifyPropertyChanged
    {
        private List<AchievementListItem> all = new List<AchievementListItem>();
        private IList<AchievementListItem> items = new List<AchievementListItem>();
        private AchievementFilter filter = AchievementFilter.All;
        private bool revealHidden;
        private string gameName;

        public AchievementListViewModel()
        {
            SetFilterCommand = new RelayCommand(p =>
            {
                AchievementFilter f;
                if (p is AchievementFilter af) Filter = af;
                else if (p != null && Enum.TryParse(p.ToString(), true, out f)) Filter = f;
            });
            ToggleRevealHiddenCommand = new RelayCommand(_ => RevealHidden = !RevealHidden);
        }

        public ICommand SetFilterCommand { get; }
        public ICommand ToggleRevealHiddenCommand { get; }

        /// <summary>Raised after the visible item list was replaced (filter change / load).</summary>
        public event Action ItemsReplaced;

        public string GameName
        {
            get => gameName;
            private set { gameName = value; OnPropertyChanged(); }
        }

        public int TotalCount => all.Count;
        public int UnlockedCount => all.Count(i => i.IsUnlocked);
        public int LockedCount => TotalCount - UnlockedCount;
        public int HiddenLockedCount => all.Count(i => i.IsHidden && i.IsLocked);
        public bool HasHiddenLocked => HiddenLockedCount > 0;
        public int Percent => AchievementListLogic.Percent(UnlockedCount, TotalCount);
        public string SummaryText => $"{UnlockedCount} / {TotalCount} unlocked";
        public string PercentText => $"{Percent}%";

        public string AllTabText => $"All ({TotalCount})";
        public string UnlockedTabText => $"Unlocked ({UnlockedCount})";
        public string LockedTabText => $"Locked ({LockedCount})";

        public bool IsFilterAll => filter == AchievementFilter.All;
        public bool IsFilterUnlocked => filter == AchievementFilter.Unlocked;
        public bool IsFilterLocked => filter == AchievementFilter.Locked;

        public string RevealHiddenText => RevealHidden
            ? $"Hide secret descriptions ({HiddenLockedCount})"
            : $"Reveal secret descriptions ({HiddenLockedCount})";

        public bool IsEmpty => items.Count == 0;

        public string EmptyText
        {
            get
            {
                if (TotalCount == 0) return "No achievement data found for this game.";
                switch (filter)
                {
                    case AchievementFilter.Unlocked: return "No achievements unlocked yet.";
                    case AchievementFilter.Locked: return "All achievements unlocked!";
                    default: return string.Empty;
                }
            }
        }

        public string ShowingText => $"Showing {items.Count} of {TotalCount}";

        public IList<AchievementListItem> Items
        {
            get => items;
            private set
            {
                items = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(EmptyText));
                OnPropertyChanged(nameof(ShowingText));
                ItemsReplaced?.Invoke();
            }
        }

        public AchievementFilter Filter
        {
            get => filter;
            set
            {
                filter = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsFilterAll));
                OnPropertyChanged(nameof(IsFilterUnlocked));
                OnPropertyChanged(nameof(IsFilterLocked));
                ApplyFilter();
            }
        }

        public bool RevealHidden
        {
            get => revealHidden;
            set
            {
                revealHidden = value;
                foreach (var i in all) i.SetRevealHidden(value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(RevealHiddenText));
            }
        }

        public void CycleFilter(int direction)
        {
            Filter = AchievementListLogic.Cycle(filter, direction);
        }

        /// <summary>Builds the list (default sort, filter All, secret descriptions concealed).</summary>
        public void Load(string game, IEnumerable<AchievementData> data)
        {
            GameName = game;
            revealHidden = false;
            all = AchievementListLogic.SortDefault(data).Select(a => new AchievementListItem(a, false)).ToList();
            filter = AchievementFilter.All;
            RaiseAllChanged();
            ApplyFilter();
        }

        /// <summary>Drops all items (and their decoded icons) when the view closes.</summary>
        public void Clear()
        {
            all = new List<AchievementListItem>();
            filter = AchievementFilter.All;
            revealHidden = false;
            RaiseAllChanged();
            Items = new List<AchievementListItem>();
        }

        private void ApplyFilter()
        {
            Items = all.Where(i => AchievementListLogic.Matches(i.Data, filter)).ToList();
        }

        private void RaiseAllChanged()
        {
            foreach (var n in new[]
            {
                nameof(TotalCount), nameof(UnlockedCount), nameof(LockedCount), nameof(HiddenLockedCount),
                nameof(HasHiddenLocked), nameof(Percent), nameof(SummaryText), nameof(PercentText),
                nameof(AllTabText), nameof(UnlockedTabText), nameof(LockedTabText), nameof(Filter),
                nameof(IsFilterAll), nameof(IsFilterUnlocked), nameof(IsFilterLocked),
                nameof(RevealHidden), nameof(RevealHiddenText)
            })
            {
                OnPropertyChanged(n);
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
