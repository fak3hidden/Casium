using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace Casium.Services
{
    /// <summary>
    /// Swaps the application-level theme dictionary at runtime and persists the choice.
    /// Every control uses DynamicResource, so a swap repaints the whole UI instantly.
    /// </summary>
    public static class ThemeManager
    {
        public static readonly string[] Available = { "Paper", "Graphite", "Volt", "Nord", "Ember" };
        public const string Default = "Paper";

        private static ResourceDictionary _current;
        private static string _currentName;

        public static string CurrentName
        {
            get { return _currentName ?? Default; }
        }

        public static event Action<string> ThemeChanged;

        public static void Apply(string name, bool persist = true)
        {
            if (string.IsNullOrWhiteSpace(name) || Array.IndexOf(Available, name) < 0)
            {
                name = Default;
            }

            var uri = new Uri(string.Format("pack://application:,,,/Casium;component/Themes/{0}.xaml", name), UriKind.Absolute);
            var dict = new ResourceDictionary { Source = uri };

            var merged = Application.Current.Resources.MergedDictionaries;

            // Remove every palette dictionary (App.xaml's design-time default uses a
            // relative "Themes/Paper.xaml" source, so match without a leading slash).
            for (int i = merged.Count - 1; i >= 0; i--)
            {
                var src = merged[i].Source;
                string s = src == null ? string.Empty : src.OriginalString.Replace('\\', '/');
                bool isPalette = ReferenceEquals(merged[i], _current)
                    || (s.IndexOf("Themes/", StringComparison.OrdinalIgnoreCase) >= 0
                        && s.IndexOf("Controls.xaml", StringComparison.OrdinalIgnoreCase) < 0);
                if (isPalette)
                {
                    merged.RemoveAt(i);
                }
            }

            // WPF resolves merged dictionaries last-to-first, so the palette must be LAST to win.
            merged.Add(dict);
            _current = dict;
            _currentName = name;

            if (persist)
            {
                Save(name);
            }

            var handler = ThemeChanged;
            if (handler != null)
            {
                handler(name);
            }
        }

        public static void ApplySaved()
        {
            Apply(Load(), persist: false);
        }

        public static Brush GetBrush(string key)
        {
            return Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;
        }

        public static Color GetColor(string key)
        {
            var scb = Application.Current.TryFindResource(key) as SolidColorBrush;
            return scb != null ? scb.Color : Colors.Transparent;
        }

        public static string GetString(string key)
        {
            return Application.Current.TryFindResource(key) as string ?? string.Empty;
        }

        public static bool IsDark
        {
            get { return GetString("Theme.MonacoBase") == "vs-dark"; }
        }

        // ---- persistence (tiny text file, no settings-designer churn) --------------

        private static string SettingsPath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Casium");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "theme");
            }
        }

        private static void Save(string name)
        {
            try { File.WriteAllText(SettingsPath, name); } catch { }
        }

        private static string Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    return File.ReadAllText(SettingsPath).Trim();
                }
            }
            catch { }
            return Default;
        }
    }
}
