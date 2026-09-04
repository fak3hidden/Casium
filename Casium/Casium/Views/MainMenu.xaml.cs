using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Casium.Services;

namespace Casium.Views
{
    public partial class MainMenu : Window
    {
        // ---------- models ---------------------------------------------------------

        private class EditorTab
        {
            public string Title { get; set; }
            public string Content { get; set; }
            public string FilePath { get; set; }
            public bool IsDirty { get; set; }
        }

        private class LogEntry
        {
            public string Time { get; set; }
            public string Message { get; set; }
            public Brush Brush { get; set; }
            public string Category { get; set; }
        }

        // ---------- state ----------------------------------------------------------

        private const string Version = "v1.0";

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private static readonly Random Rnd = new Random();
        private static Brush GrayBrush   { get { return ThemeManager.GetBrush("Text.Secondary"); } }
        private static Brush WhiteBrush  { get { return ThemeManager.GetBrush("Text.Primary"); } }
        private static Brush GreenBrush  { get { return ThemeManager.GetBrush("Status.Ok"); } }
        private static Brush RedBrush    { get { return ThemeManager.GetBrush("Status.Err"); } }
        private static Brush YellowBrush { get { return ThemeManager.GetBrush("Status.Warn"); } }
        private static Brush PurpleBrush { get { return ThemeManager.GetBrush("Accent.Text"); } }
        private static Brush IdleBrush   { get { return ThemeManager.GetBrush("Status.Idle"); } }

        private readonly List<EditorTab> _tabs = new List<EditorTab>();
        private EditorTab _selectedTab;
        private int _tabCounter;
        private bool _syncingEditor;
        private bool _highlighting;

        private readonly List<LogEntry> _allLogs = new List<LogEntry>();
        private readonly ObservableCollection<LogEntry> _visibleLogs = new ObservableCollection<LogEntry>();
        private bool _outputOnly;
        private bool _showTimestamps = true;

        private readonly Dictionary<string, FrameworkElement> _views = new Dictionary<string, FrameworkElement>();
        private readonly List<Button> _navButtons = new List<Button>();

        private readonly DispatcherTimer _cursorTimer = new DispatcherTimer();
        private readonly List<string> _recent = new List<string>();
        private bool _consoleCollapsed;
        private double _consoleHeight = 170;

        private bool _attached;
        private bool _attaching;
        private bool _monacoReady;
        private bool _useMonaco;
        private bool _autoAttach = false;
        private string _username = "—";

        private static readonly Regex LuaToken = new Regex(
            @"(?<comment>--\[\[.*?\]\]|--[^\n]*)" +
            @"|(?<string>""([^""\\\n]|\\.)*""|'([^'\\\n]|\\.)*')" +
            @"|(?<keyword>\b(local|function|end|if|then|else|elseif|for|while|do|return|break|in|repeat|until|nil|true|false|and|or|not)\b)" +
            @"|(?<number>\b\d+(\.\d+)?\b)" +
            @"|(?<builtin>\b(game|workspace|script|print|pairs|ipairs|task|wait|tostring|tonumber|require|loadstring|Instance|Vector3|CFrame|Enum|typeof|select|unpack|pcall|tick|os|math|string|table)\b)",
            RegexOptions.Compiled);

        private static Brush LuaDefault { get { return ThemeManager.GetBrush("Editor.Text"); } }
        private static Brush LuaComment { get { return ThemeManager.GetBrush("Syntax.Comment"); } }
        private static Brush LuaString  { get { return ThemeManager.GetBrush("Syntax.String"); } }
        private static Brush LuaKeyword { get { return ThemeManager.GetBrush("Syntax.Keyword"); } }
        private static Brush LuaNumber  { get { return ThemeManager.GetBrush("Syntax.Number"); } }
        private static Brush LuaBuiltin { get { return ThemeManager.GetBrush("Syntax.Builtin"); } }

        private const string DefaultScript = "print(\"Hello from Casium\")\n";

        // ---------- construction ---------------------------------------------------

        public MainMenu()
        {
            InitializeComponent();
            Loaded += MainMenu_Loaded;
        }

        public MainMenu(string username) : this()
        {
            if (!string.IsNullOrWhiteSpace(username))
            {
                _username = username;
            }
        }

        private async void MainMenu_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    int round = 2;
                    DwmSetWindowAttribute(hwnd, 33, ref round, 4);
                }
            }
            catch { }

            _ = InitMonacoAsync();

            StartUserRun.Text = string.Format("Signed in as {0}.", _username);
            AccountNameText.Text = _username;
            ThemeStatusText.Text = ThemeManager.CurrentName;
            ThemeManager.ThemeChanged += OnThemeChanged;
            Closed += (ss, ee) => ThemeManager.ThemeChanged -= OnThemeChanged;
            StateChanged += (ss, ee) => MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            PreviewKeyDown += MainMenu_PreviewKeyDown;

            BuildViews();
            BuildSettings();
            BuildThemePicker();
            LoadRecent();
            RefreshExplorer();

            EditorScroll.SizeChanged += (ss, ee) => RefreshPageWidth();
            LogList.ItemsSource = _visibleLogs;

            RefreshTabStrip();
            SwitchView("StartView");

            AddLog("sys", "Welcome to Casium.");

            _cursorTimer.Interval = TimeSpan.FromMilliseconds(400);
            _cursorTimer.Tick += async (ss, ee) => await PollMonacoCursorAsync();
            _cursorTimer.Start();

            if (_autoAttach)
            {
                await AttachSequence();
            }
        }

        private async void MainMenu_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            if (e.Key == Key.F5)
            {
                e.Handled = true;
                await ExecuteCurrentTab();
            }
            else if (ctrl && e.Key == Key.T)
            {
                e.Handled = true;
                await SyncMonacoToTabAsync();
                NewTab(string.Format("Script{0}", ++_tabCounter), DefaultScript);
            }
            else if (ctrl && e.Key == Key.S)
            {
                e.Handled = true;
                await SaveCurrentTab();
            }
            else if (ctrl && e.Key == Key.O)
            {
                e.Handled = true;
                await OpenFile();
            }
            else if (ctrl && e.Key == Key.W && _selectedTab != null)
            {
                e.Handled = true;
                await CloseTabAsync(_selectedTab);
            }
        }

        // ---------- monaco editor (WebView2) ------------------------------------------

        private static string EnsureMonacoPage()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Casium");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "monaco.html");
            using (Stream src = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("Casium.Resources.Monaco.html"))
            {
                if (src == null)
                {
                    throw new Exception("embedded editor page is missing");
                }
                using (FileStream dst = File.Create(path))
                {
                    src.CopyTo(dst);
                }
            }
            return path;
        }

        private async Task InitMonacoAsync()
        {
            try
            {
                string page = EnsureMonacoPage();

                await MonacoView.EnsureCoreWebView2Async();

                var navTcs = new TaskCompletionSource<bool>();
                EventHandler<CoreWebView2NavigationCompletedEventArgs> navHandler = null;
                navHandler = (ss, ee) =>
                {
                    MonacoView.NavigationCompleted -= navHandler;
                    navTcs.TrySetResult(ee.IsSuccess);
                };
                MonacoView.NavigationCompleted += navHandler;
                MonacoView.Source = new Uri(page);

                Task finished = await Task.WhenAny(navTcs.Task, Task.Delay(15000));
                if (finished != navTcs.Task || !navTcs.Task.Result)
                {
                    throw new Exception("editor page failed to load");
                }

                bool ready = false;
                for (int i = 0; i < 16; i++)
                {
                    try
                    {
                        string probe = await MonacoView.ExecuteScriptAsync(
                            "window.Bubble && window.Bubble.editor ? 'ok' : ''");
                        if (DecodeJsString(probe) == "ok")
                        {
                            ready = true;
                            break;
                        }
                    }
                    catch { }
                    await Task.Delay(750);
                }
                if (!ready)
                {
                    throw new Exception("Monaco engine did not start (internet needed for the CDN)");
                }

                await ApplyMonacoThemeAsync();
                await MonacoView.ExecuteScriptAsync("window.Bubble.setLanguage('lua')");

                _monacoReady = true;
                _useMonaco = true;

                MonacoView.Visibility = Visibility.Visible;
                EditorBox.Visibility = Visibility.Collapsed;
                GutterColumn.Width = new GridLength(0);
                LineNumbers.Visibility = Visibility.Collapsed;

                await SyncTabToMonacoAsync();
                AddLog("ok", "Monaco editor ready.");
            }
            catch (Exception ex)
            {
                _monacoReady = false;
                _useMonaco = false;
                AddLog("warn", "Monaco unavailable, using built-in editor. (" + ex.Message + ")");
            }
        }

        private async Task<string> GetMonacoCodeAsync()
        {
            string raw = await MonacoView.ExecuteScriptAsync("window.Bubble.getCode()");
            return DecodeJsString(raw);
        }

        private Task SetMonacoCodeAsync(string code)
        {
            return MonacoView.ExecuteScriptAsync(
                "window.Bubble.setCode(" + EncodeJsString(code ?? string.Empty) + ")");
        }

        private async Task SyncMonacoToTabAsync()
        {
            if (!_monacoReady)
            {
                return;
            }
            EditorTab tab = _selectedTab;
            if (tab == null)
            {
                return;
            }
            try
            {
                string code = await GetMonacoCodeAsync();
                if (code == null)
                {
                    return;
                }
                tab.Content = code;
            }
            catch { }
        }

        private async Task SyncTabToMonacoAsync()
        {
            if (!_monacoReady || _selectedTab == null)
            {
                return;
            }
            try
            {
                await SetMonacoCodeAsync(_selectedTab.Content);
            }
            catch { }
        }

        private async Task PollMonacoCursorAsync()
        {
            if (!_monacoReady || _currentView != "ExecutorView")
            {
                return;
            }
            try
            {
                string raw = await MonacoView.ExecuteScriptAsync(
                    "(function(){var e=window.Bubble.editor;if(!e)return '';var p=e.getPosition();return p.lineNumber+','+p.column;})()");
                string pos = DecodeJsString(raw);
                if (string.IsNullOrEmpty(pos))
                {
                    return;
                }
                string[] parts = pos.Split(',');
                if (parts.Length == 2)
                {
                    LnColText.Text = string.Format("Ln {0}, Col {1}", parts[0], parts[1]);
                }
            }
            catch { }
        }

        private static string EncodeJsString(string value)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in value ?? string.Empty)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u" + ((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static string DecodeJsString(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return null;
            }
            string t = raw.Trim();
            if (t == "null")
            {
                return null;
            }
            if (t.Length >= 2 && t[0] == '"' && t[t.Length - 1] == '"')
            {
                var sb = new StringBuilder();
                for (int i = 1; i < t.Length - 1; i++)
                {
                    char c = t[i];
                    if (c == '\\' && i + 1 < t.Length - 1)
                    {
                        char n = t[++i];
                        switch (n)
                        {
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'u':
                                if (i + 4 < t.Length - 1)
                                {
                                    int code;
                                    if (int.TryParse(t.Substring(i + 1, 4),
                                        System.Globalization.NumberStyles.HexNumber, null, out code))
                                    {
                                        sb.Append((char)code);
                                        i += 4;
                                    }
                                    else
                                    {
                                        sb.Append(n);
                                    }
                                }
                                else
                                {
                                    sb.Append(n);
                                }
                                break;
                            default: sb.Append(n); break;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                return sb.ToString();
            }
            return t;
        }

        // ---------- custom title bar ------------------------------------------------

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && WindowState == WindowState.Normal)
            {
                DragMove();
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // ---------- navigation -------------------------------------------------------

        private void BuildViews()
        {
            _views["StartView"] = StartView;
            _views["ExecutorView"] = ExecutorView;
            _views["SettingsView"] = SettingsView;
        }

        private string _currentView = "StartView";

        private void SwitchView(string key)
        {
            _currentView = key;
            foreach (var pair in _views)
            {
                pair.Value.Visibility = pair.Key == key ? Visibility.Visible : Visibility.Collapsed;
            }
            if (key != "ExecutorView")
            {
                _selectedTab = null;
                LnColText.Text = string.Empty;
            }
            StatusTabText.Text = key == "StartView" ? "Start"
                : key == "SettingsView" ? "Settings"
                : (_selectedTab != null ? _selectedTab.Title : string.Empty);
            RefreshTabStrip();
            if (key == "ExecutorView" && _selectedTab != null && !_useMonaco)
            {
                EditorBox.Focus();
            }
        }

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            SwitchView("SettingsView");
        }

        private void StartTab_Click(object sender, MouseButtonEventArgs e)
        {
            _ = SyncMonacoToTabAsync();
            SwitchView("StartView");
        }

        private void SettingsTab_Click(object sender, MouseButtonEventArgs e)
        {
            _ = SyncMonacoToTabAsync();
            SwitchView("SettingsView");
        }

        private void CollapseSidebarButton_Click(object sender, RoutedEventArgs e)
        {
            bool hide = Sidebar.Visibility == Visibility.Visible;
            Sidebar.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
            SidebarColumn.Width = hide ? new GridLength(0) : new GridLength(250);
            ExpandSidebarButton.Visibility = hide ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ToggleConsoleButton_Click(object sender, RoutedEventArgs e)
        {
            _consoleCollapsed = !_consoleCollapsed;
            if (_consoleCollapsed)
            {
                _consoleHeight = Math.Max(ConsoleRow.ActualHeight, 80);
                ConsoleRow.Height = new GridLength(0);
                ConsoleChevron.Data = Geometry.Parse("M6,9 L12,15 L18,9");
            }
            else
            {
                ConsoleRow.Height = new GridLength(_consoleHeight);
                ConsoleChevron.Data = Geometry.Parse("M6,15 L12,9 L18,15");
            }
        }

        // ---------- explorer -----------------------------------------------------------

        private static string ScriptsDir
        {
            get
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string AutoExecDir
        {
            get
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "autoexec");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private void RefreshExplorer()
        {
            FillExplorer(ScriptsList, ScriptsDir);
            FillExplorer(AutoExecList, AutoExecDir);
        }

        private void FillExplorer(StackPanel panel, string dir)
        {
            panel.Children.Clear();
            string filter = (FilterBox.Text ?? string.Empty).Trim().ToLowerInvariant();
            string[] files;
            try
            {
                files = Directory.GetFiles(dir)
                    .Where(f => f.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)
                             || f.EndsWith(".luau", StringComparison.OrdinalIgnoreCase)
                             || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch
            {
                files = new string[0];
            }

            foreach (var file in files)
            {
                string name = Path.GetFileName(file);
                if (filter.Length > 0 && !name.ToLowerInvariant().Contains(filter))
                {
                    continue;
                }
                panel.Children.Add(MakeFileRow(name, file));
            }

            if (panel.Children.Count == 0)
            {
                var empty = new TextBlock { Text = filter.Length > 0 ? "No matches" : "Empty", FontSize = 12, Margin = new Thickness(10, 3, 0, 3) };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
                panel.Children.Add(empty);
            }
        }

        private Button MakeFileRow(string name, string path)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M6,2 H14 L20,8 V22 H6 Z M14,2 V8 H20"),
                StrokeThickness = 1.6, Width = 12, Height = 12, Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center
            };
            icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Text.Secondary");
            row.Children.Add(icon);
            var label = new TextBlock { Text = name, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            row.Children.Add(label);

            var btn = new Button { Style = (Style)FindResource("ExplorerRow"), Content = row, Tag = path, ToolTip = path };
            btn.Click += ExplorerFile_Click;
            return btn;
        }

        private async void ExplorerFile_Click(object sender, RoutedEventArgs e)
        {
            string path = (string)((Button)sender).Tag;
            await OpenPath(path);
        }

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            FilterHint.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshExplorer();
        }

        private string FolderFor(object sender)
        {
            return (string)((Button)sender).Tag == "autoexec" ? AutoExecDir : ScriptsDir;
        }

        private async void NewFileInFolder_Click(object sender, RoutedEventArgs e)
        {
            string dir = FolderFor(sender);
            string path = null;
            for (int i = 1; i < 1000; i++)
            {
                string candidate = Path.Combine(dir, string.Format("script{0}.lua", i));
                if (!File.Exists(candidate))
                {
                    path = candidate;
                    break;
                }
            }
            if (path == null)
            {
                return;
            }
            try
            {
                File.WriteAllText(path, DefaultScript);
            }
            catch (Exception ex)
            {
                AddLog("err", "Could not create file: " + ex.Message);
                return;
            }
            RefreshExplorer();
            if (dir == AutoExecDir)
            {
                AutoExecHeader.IsChecked = true;
                AutoExecList.Visibility = Visibility.Visible;
            }
            await OpenPath(path);
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", FolderFor(sender));
            }
            catch (Exception ex)
            {
                AddLog("err", "Could not open folder: " + ex.Message);
            }
        }

        private void ExplorerHeader_Click(object sender, RoutedEventArgs e)
        {
            var header = (System.Windows.Controls.Primitives.ToggleButton)sender;
            var list = header == ScriptsHeader ? ScriptsList : AutoExecList;
            list.Visibility = header.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------- recent -------------------------------------------------------------

        private static string RecentPath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Casium");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "recent.txt");
            }
        }

        private void LoadRecent()
        {
            _recent.Clear();
            try
            {
                if (File.Exists(RecentPath))
                {
                    _recent.AddRange(File.ReadAllLines(RecentPath).Where(l => l.Length > 0 && File.Exists(l)).Take(8));
                }
            }
            catch { }
            RenderRecent();
        }

        private void PushRecent(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            _recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            _recent.Insert(0, path);
            while (_recent.Count > 8)
            {
                _recent.RemoveAt(_recent.Count - 1);
            }
            try { File.WriteAllLines(RecentPath, _recent); } catch { }
            RenderRecent();
        }

        private void RenderRecent()
        {
            RecentList.Children.Clear();
            RecentEmpty.Visibility = _recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var path in _recent)
            {
                var b = MakeFileRow(Path.GetFileName(path), path);
                b.Height = 32;
                b.FontSize = 13;
                b.Padding = new Thickness(10, 0, 10, 0);
                b.HorizontalAlignment = HorizontalAlignment.Left;
                b.Foreground = ThemeManager.GetBrush("Accent.Text");
                b.SetResourceReference(Button.ForegroundProperty, "Accent.Text");
                RecentList.Children.Add(b);
            }
        }

        // ---------- editor tabs -------------------------------------------------------

        private void NewTab(string title, string content, string path = null)
        {
            var tab = new EditorTab { Title = title, Content = content ?? string.Empty, FilePath = path };
            _tabs.Add(tab);
            _currentView = "ExecutorView";
            foreach (var pair in _views)
            {
                pair.Value.Visibility = pair.Key == "ExecutorView" ? Visibility.Visible : Visibility.Collapsed;
            }
            SelectTab(tab);
        }

        private void SelectTab(EditorTab tab)
        {
            if (tab == null || !_tabs.Contains(tab))
            {
                return;
            }
            _selectedTab = tab;
            _currentView = "ExecutorView";
            foreach (var pair in _views)
            {
                pair.Value.Visibility = pair.Key == "ExecutorView" ? Visibility.Visible : Visibility.Collapsed;
            }
            LoadTabContent();
            RefreshTabStrip();
            _ = SyncTabToMonacoAsync();
        }

        private void LoadTabContent()
        {
            _syncingEditor = true;
            try
            {
                var doc = new FlowDocument { PagePadding = new Thickness(0) };
                doc.Blocks.Add(BuildLuaParagraph(_selectedTab.Content));
                doc.PageWidth = ComputePageWidth(_selectedTab.Content);
                EditorBox.Document = doc;
            }
            finally
            {
                _syncingEditor = false;
            }
            UpdateLineNumbers();
            UpdateLnCol();
            StatusTabText.Text = _selectedTab.Title;
        }

        private Border MakeTopTab(string title, Geometry icon, bool active, MouseButtonEventHandler onClick, EditorTab tab)
        {
            var border = new Border { Style = (Style)FindResource("DocTab"), Tag = tab };
            NavState.SetIsActive(border, active);
            border.MouseLeftButtonDown += onClick;

            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var ic = new System.Windows.Shapes.Path { Data = icon, StrokeThickness = 1.6, Width = 13, Height = 13, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
            ic.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, active ? "Text.Primary" : "Text.Secondary");
            row.Children.Add(ic);
            var label = new TextBlock { Text = title, FontSize = 13, Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, active ? "Text.Primary" : "Text.Secondary");
            row.Children.Add(label);

            if (tab != null)
            {
                var close = new Button
                {
                    Tag = tab,
                    Style = (Style)FindResource("Button.Icon"),
                    Width = 22, Height = 22,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Close"
                };
                if (tab.IsDirty && !active)
                {
                    var dot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6 };
                    dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Text.Secondary");
                    close.Content = dot;
                }
                else
                {
                    var x = new System.Windows.Shapes.Path { Data = Geometry.Parse("M6,6 L18,18 M18,6 L6,18"), StrokeThickness = 1.6, Width = 9, Height = 9, Stretch = Stretch.Uniform };
                    x.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { Source = close });
                    close.Content = x;
                }
                close.Click += CloseTab_Click;
                row.Children.Add(close);
            }
            else
            {
                row.Margin = new Thickness(0, 0, 6, 0);
            }

            border.Child = row;
            return border;
        }

        private void RefreshTabStrip()
        {
            TabStripPanel.Children.Clear();

            TabStripPanel.Children.Add(MakeTopTab("Start",
                Geometry.Parse("M12,2 L14.9,8.6 L22,9.3 L16.7,14.1 L18.2,21 L12,17.5 L5.8,21 L7.3,14.1 L2,9.3 L9.1,8.6 Z"),
                _currentView == "StartView", StartTab_Click, null));

            var fileIcon = Geometry.Parse("M6,2 H14 L20,8 V22 H6 Z M14,2 V8 H20");
            foreach (var tab in _tabs)
            {
                bool active = _currentView == "ExecutorView" && tab == _selectedTab;
                TabStripPanel.Children.Add(MakeTopTab(tab.Title, fileIcon, active, SelectTab_Click, tab));
            }

            if (_currentView == "SettingsView")
            {
                TabStripPanel.Children.Add(MakeTopTab("Settings",
                    Geometry.Parse("M12,15.5 A3.5,3.5 0 1,0 12,8.5 A3.5,3.5 0 1,0 12,15.5 Z M12,2 V5 M12,19 V22 M2,12 H5 M19,12 H22 M4.9,4.9 L7,7 M17,17 L19.1,19.1 M4.9,19.1 L7,17 M17,7 L19.1,4.9"),
                    true, SettingsTab_Click, null));
            }
        }

        private async void SelectTab_Click(object sender, MouseButtonEventArgs e)
        {
            await SyncMonacoToTabAsync();
            SelectTab((EditorTab)((Border)sender).Tag);
        }

        private async void AddTabButton_Click(object sender, RoutedEventArgs e)
        {
            await SyncMonacoToTabAsync();
            NewTab(string.Format("Script{0}", ++_tabCounter + 0), "-- New script\n");
            SwitchView("ExecutorView");
        }

        private async void CloseTab_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            await CloseTabAsync((EditorTab)((Button)sender).Tag);
        }

        private async Task CloseTabAsync(EditorTab tab)
        {
            await SyncMonacoToTabAsync();
            int index = _tabs.IndexOf(tab);
            if (index < 0)
            {
                return;
            }
            _tabs.RemoveAt(index);
            if (_tabs.Count == 0)
            {
                SwitchView("StartView");
            }
            else if (_selectedTab == tab)
            {
                SelectTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
            }
            else
            {
                RefreshTabStrip();
            }
        }

        // ---------- editor text + highlighting ------------------------------------------

        private string GetEditorText()
        {
            return new TextRange(EditorBox.Document.ContentStart, EditorBox.Document.ContentEnd).Text
                .TrimEnd('\r', '\n');
        }

        private void EditorBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_syncingEditor || _highlighting || _selectedTab == null)
            {
                return;
            }

            _selectedTab.Content = GetEditorText();
            _selectedTab.IsDirty = true;

            _highlighting = true;
            try
            {
                int offset = EditorBox.Document.ContentStart.GetOffsetToPosition(EditorBox.CaretPosition);
                var doc = new FlowDocument { PagePadding = new Thickness(0) };
                doc.Blocks.Add(BuildLuaParagraph(_selectedTab.Content));
                doc.PageWidth = ComputePageWidth(_selectedTab.Content);
                EditorBox.Document = doc;
                var pos = EditorBox.Document.ContentStart.GetPositionAtOffset(offset);
                EditorBox.CaretPosition = pos ?? EditorBox.Document.ContentEnd;
            }
            catch
            {
                // Highlighting is best-effort; the plain text is already stored.
            }
            finally
            {
                _highlighting = false;
            }

            RefreshTabStrip();
            UpdateLineNumbers();
            UpdateLnCol();
        }

        private static Paragraph BuildLuaParagraph(string code)
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0),
                LineHeight = 20,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight
            };

            if (string.IsNullOrEmpty(code))
            {
                return paragraph;
            }

            int last = 0;
            foreach (Match m in LuaToken.Matches(code))
            {
                if (m.Index > last)
                {
                    paragraph.Inlines.Add(new Run(code.Substring(last, m.Index - last)) { Foreground = LuaDefault });
                }

                Brush color = LuaDefault;
                if (m.Groups["comment"].Success) color = LuaComment;
                else if (m.Groups["string"].Success) color = LuaString;
                else if (m.Groups["keyword"].Success) color = LuaKeyword;
                else if (m.Groups["number"].Success) color = LuaNumber;
                else if (m.Groups["builtin"].Success) color = LuaBuiltin;

                paragraph.Inlines.Add(new Run(m.Value) { Foreground = color });
                last = m.Index + m.Length;
            }

            if (last < code.Length)
            {
                paragraph.Inlines.Add(new Run(code.Substring(last)) { Foreground = LuaDefault });
            }

            return paragraph;
        }

        private double ComputePageWidth(string code)
        {
            // RichTextBox has no TextWrapping property: the page is sized so
            // long lines extend past the viewport (outer ScrollViewer scrolls)
            // instead of wrapping and breaking the line-number gutter.
            double viewport = EditorScroll.ViewportWidth;
            if (viewport <= 0 || double.IsNaN(viewport))
            {
                viewport = Math.Max(ActualWidth - 560, 320);
            }
            double contentWidth = Math.Max(viewport - 52 - 28, 120);
            int longest = 0;
            if (!string.IsNullOrEmpty(code))
            {
                foreach (var line in code.Split('\n'))
                {
                    longest = Math.Max(longest, line.TrimEnd('\r').Length);
                }
            }
            return Math.Max(contentWidth, longest * 9.0 + 16);
        }

        private void RefreshPageWidth()
        {
            if (EditorBox.Document != null && _selectedTab != null)
            {
                EditorBox.Document.PageWidth = ComputePageWidth(_selectedTab.Content);
            }
        }

        private void UpdateLineNumbers()
        {
            int lines = 1;
            if (_selectedTab != null && !string.IsNullOrEmpty(_selectedTab.Content))
            {
                lines = _selectedTab.Content.Split('\n').Length;
            }
            LineNumbers.Text = string.Join("\n", Enumerable.Range(1, Math.Max(lines, 1)));
        }

        private void UpdateLnCol()
        {
            try
            {
                string before = new TextRange(EditorBox.Document.ContentStart, EditorBox.CaretPosition).Text;
                int line = before.Count(c => c == '\n') + 1;
                int col = before.Length - (before.LastIndexOf('\n') + 1) + 1;
                LnColText.Text = string.Format("Ln {0}, Col {1}", line, col);
            }
            catch
            {
                LnColText.Text = "Ln 1, Col 1";
            }
        }

        private void EditorBox_KeyUp(object sender, KeyEventArgs e)
        {
            UpdateLnCol();
        }

        private void EditorBox_MouseUp(object sender, MouseButtonEventArgs e)
        {
            UpdateLnCol();
        }

        // ---------- console ---------------------------------------------------------------

        private void AddLog(string category, string message)
        {
            Brush brush = BrushForCategory(category);

            var entry = new LogEntry
            {
                Time = _showTimestamps ? string.Format("[{0:HH:mm:ss}] ", DateTime.Now) : string.Empty,
                Message = message,
                Brush = brush,
                Category = category
            };

            _allLogs.Add(entry);
            if (!_outputOnly || category == "exec" || category == "ok" || category == "err")
            {
                _visibleLogs.Add(entry);
                LogList.ScrollIntoView(entry);
            }
        }

        private void RefreshLogView()
        {
            _visibleLogs.Clear();
            foreach (var entry in _allLogs)
            {
                if (!_outputOnly || entry.Category == "exec" || entry.Category == "ok" || entry.Category == "err")
                {
                    _visibleLogs.Add(entry);
                }
            }
            if (_visibleLogs.Count > 0)
            {
                LogList.ScrollIntoView(_visibleLogs[_visibleLogs.Count - 1]);
            }
        }

        private void ConsoleTab_Click(object sender, RoutedEventArgs e)
        {
            _outputOnly = false;
            NavState.SetIsActive(ConsoleTabButton, true);
            NavState.SetIsActive(OutputTabButton, false);
            RefreshLogView();
        }

        private void OutputTab_Click(object sender, RoutedEventArgs e)
        {
            _outputOnly = true;
            NavState.SetIsActive(OutputTabButton, true);
            NavState.SetIsActive(ConsoleTabButton, false);
            RefreshLogView();
        }

        private void ClearConsoleButton_Click(object sender, RoutedEventArgs e)
        {
            _allLogs.Clear();
            _visibleLogs.Clear();
        }

        private void CommandInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }
            string cmd = CommandInput.Text.Trim();
            CommandInput.Clear();
            if (cmd.Length == 0)
            {
                return;
            }

            AddLog("cmd", "> " + cmd);
            HandleCommand(cmd.ToLowerInvariant());
            e.Handled = true;
        }

        private async void HandleCommand(string cmd)
        {
            switch (cmd)
            {
                case "help":
                    AddLog("sys", "Commands: help, clear, attach, detach, inject, execute, version");
                    break;
                case "clear":
                    _allLogs.Clear();
                    _visibleLogs.Clear();
                    break;
                case "attach":
                case "inject":
                    await AttachSequence();
                    break;
                case "detach":
                    Detach();
                    break;
                case "execute":
                    await ExecuteCurrentTab();
                    break;
                case "version":
                    AddLog("sys", "Casium " + Version);
                    break;
                default:
                    AddLog("warn", string.Format("Unknown command '{0}'. Type 'help'.", cmd));
                    break;
            }
        }

        // ---------- attach / execute ---------------------------------------------------------

        private async Task AttachSequence()
        {
            if (_attached || _attaching)
            {
                return;
            }
            _attaching = true;
            InjectButton.IsEnabled = false;
            try
            {
                AddLog("sys", "Attaching to Roblox...");
                await Task.Delay(650);
                AddLog("ok", "Attached.");

                _attached = true;
                SetAttachedUi(true);
            }
            finally
            {
                _attaching = false;
                InjectButton.IsEnabled = true;
            }
        }

        private void Detach()
        {
            if (!_attached)
            {
                return;
            }
            _attached = false;
            SetAttachedUi(false);
            AddLog("sys", "Detached.");
        }

        private void SetAttachedUi(bool on)
        {
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, on ? "Status.Ok" : "Status.Err");
            AttachedText.Text = on ? "Roblox" : "No client";
            StartAttachRun.Text = on ? "Attached to Roblox." : "No client attached.";
            StartAttachRun.SetResourceReference(Run.ForegroundProperty, on ? "Status.Ok" : "Text.Tertiary");
            InjectLabel.Text = on ? "Detach" : "Attach";
        }

        private async void InjectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_attached)
            {
                Detach();
            }
            else
            {
                await AttachSequence();
            }
        }

        private async void ExecuteButton_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteCurrentTab();
        }

        private async Task ExecuteCurrentTab()
        {
            if (_selectedTab == null || _currentView != "ExecutorView")
            {
                AddLog("warn", "Open a script first.");
                return;
            }
            await SyncMonacoToTabAsync();
            if (!_attached)
            {
                AddLog("err", "No client attached. Press Attach first.");
                return;
            }
            AddLog("exec", string.Format("Executing '{0}'...", _selectedTab.Title));
            await Task.Delay(300);
            AddLog("ok", "Executed.");
        }

        // ---------- open / save ---------------------------------------------------------------

        private async void OpenFileButton_Click(object sender, RoutedEventArgs e)
        {
            await OpenFile();
        }

        private async Task OpenFile()
        {
            await SyncMonacoToTabAsync();
            var dialog = new OpenFileDialog
            {
                Filter = "Lua files (*.lua;*.luau)|*.lua;*.luau|Text files (*.txt)|*.txt|All files (*.*)|*.*"
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }
            await OpenPath(dialog.FileName);
        }

        private async Task OpenPath(string path)
        {
            await SyncMonacoToTabAsync();
            var existing = _tabs.FirstOrDefault(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                SelectTab(existing);
                return;
            }
            try
            {
                NewTab(Path.GetFileName(path), File.ReadAllText(path), path);
                PushRecent(path);
                AddLog("ok", string.Format("Opened '{0}'.", Path.GetFileName(path)));
            }
            catch (Exception ex)
            {
                AddLog("err", "Could not open file: " + ex.Message);
            }
        }

        private async Task SaveCurrentTab()
        {
            if (_selectedTab == null)
            {
                return;
            }
            await SyncMonacoToTabAsync();
            try
            {
                if (string.IsNullOrEmpty(_selectedTab.FilePath))
                {
                    var dialog = new SaveFileDialog
                    {
                        InitialDirectory = ScriptsDir,
                        FileName = _selectedTab.Title.EndsWith(".lua") || _selectedTab.Title.EndsWith(".luau") ? _selectedTab.Title : _selectedTab.Title + ".lua",
                        Filter = "Lua files (*.lua)|*.lua|All files (*.*)|*.*"
                    };
                    if (dialog.ShowDialog(this) != true)
                    {
                        return;
                    }
                    _selectedTab.FilePath = dialog.FileName;
                    _selectedTab.Title = Path.GetFileName(dialog.FileName);
                }
                File.WriteAllText(_selectedTab.FilePath, _selectedTab.Content ?? string.Empty);
                _selectedTab.IsDirty = false;
                RefreshTabStrip();
                RefreshExplorer();
                PushRecent(_selectedTab.FilePath);
                StatusTabText.Text = _selectedTab.Title;
                AddLog("ok", string.Format("Saved '{0}'.", _selectedTab.Title));
            }
            catch (Exception ex)
            {
                AddLog("err", "Could not save file: " + ex.Message);
            }
        }

        // ---------- settings view --------------------------------------------------------------------------

        private void BuildSettings()
        {
            AddSetting("alwaysontop", "Always on top", "Keep Casium above other windows.", false);
            AddSetting("autoattach", "Auto attach", "Attach to the client automatically on startup.", false);
            AddSetting("linenumbers", "Line numbers", "Show the gutter in the built-in editor.", true);
            AddSetting("timestamps", "Console timestamps", "Prefix console lines with the time.", true);
        }

        private void AddSetting(string key, string title, string desc, bool def)
        {
            var row = new Border
            {
                BorderBrush = ThemeManager.GetBrush("App.Border"),
                BorderThickness = new Thickness(0, SettingsPanel.Children.Count == 0 ? 0 : 1, 0, 0),
                Padding = new Thickness(12, 12, 12, 12)
            };
            row.SetResourceReference(Border.BorderBrushProperty, "App.Border");

            var text = new StackPanel();
            var t1 = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold };
            t1.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
            var t2 = new TextBlock { Text = desc, FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
            t2.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            text.Children.Add(t1);
            text.Children.Add(t2);

            var check = new CheckBox
            {
                Content = text,
                Tag = key,
                IsChecked = def,
                Style = (Style)FindResource("Switch")
            };
            check.Checked += SettingToggle_Changed;
            check.Unchecked += SettingToggle_Changed;

            row.Child = check;
            SettingsPanel.Children.Add(row);
        }

        // ---------- themes -------------------------------------------------------------------------------

        private void BuildThemePicker()
        {
            ThemePanel.Children.Clear();
            foreach (var name in ThemeManager.Available)
            {
                var dict = new ResourceDictionary
                {
                    Source = new Uri(string.Format("pack://application:,,,/Casium;component/Themes/{0}.xaml", name))
                };

                var swatch = new Border
                {
                    Width = 96, Height = 64,
                    CornerRadius = new CornerRadius(8),
                    Background = (Brush)dict["App.Background"],
                    BorderBrush = (Brush)dict["App.BorderStrong"],
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8),
                    ClipToBounds = true
                };
                var preview = new Grid();
                preview.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                preview.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                preview.Children.Add(new Border { Background = (Brush)dict["App.Surface"], CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 4, 0) });
                var right = new StackPanel();
                right.Children.Add(new Border { Height = 6, Background = (Brush)dict["Accent"], CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 20, 5) });
                right.Children.Add(new Border { Height = 5, Background = (Brush)dict["Text.Tertiary"], CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 8, 4), Opacity = 0.6 });
                right.Children.Add(new Border { Height = 5, Background = (Brush)dict["Text.Tertiary"], CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 30, 0), Opacity = 0.6 });
                Grid.SetColumn(right, 1);
                preview.Children.Add(right);
                swatch.Child = preview;

                var label = new TextBlock { Text = name, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
                label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");

                var inner = new StackPanel();
                inner.Children.Add(swatch);
                inner.Children.Add(label);

                var tile = new Button
                {
                    Tag = name,
                    Content = inner,
                    Style = (Style)FindResource("ThemeTile")
                };
                tile.Click += ThemeTile_Click;
                ThemePanel.Children.Add(tile);
            }
            HighlightThemeTile(ThemeManager.CurrentName);
        }

        private void HighlightThemeTile(string name)
        {
            foreach (Button tile in ThemePanel.Children)
            {
                NavState.SetIsActive(tile, (string)tile.Tag == name);
            }
        }

        private void ThemeTile_Click(object sender, RoutedEventArgs e)
        {
            string name = (string)((Button)sender).Tag;
            try
            {
                ThemeManager.Apply(name);
                AddLog("sys", "Theme set to " + name + ".");
            }
            catch (Exception ex)
            {
                AddLog("err", "Could not apply theme: " + ex.Message);
            }
        }

        private async void OnThemeChanged(string name)
        {
            ThemeStatusText.Text = name;
            HighlightThemeTile(name);

            if (_selectedTab != null)
            {
                LoadTabContent();
            }
            RefreshTabStrip();
            foreach (var log in _allLogs) log.Brush = BrushForCategory(log.Category);
            RefreshLogView();

            await ApplyMonacoThemeAsync();
        }

        private async Task ApplyMonacoThemeAsync()
        {
            if (!_monacoReady && MonacoView.CoreWebView2 == null)
            {
                return;
            }
            try
            {
                string bg = ColorHex(ThemeManager.GetColor("Editor.Background"));
                string fg = ColorHex(ThemeManager.GetColor("Editor.Text"));
                string line = ColorHex(ThemeManager.GetColor("Editor.LineHighlight"));
                string gutter = ColorHex(ThemeManager.GetColor("Editor.Gutter"));
                string baseTheme = ThemeManager.GetString("Theme.MonacoBase");
                string js =
                    "monaco.editor.defineTheme('casium', { base: '" + baseTheme + "', inherit: true, rules: [" +
                    "{ token: 'comment', foreground: '" + ColorHex(ThemeManager.GetColor("Syntax.Comment")).Substring(1) + "' }," +
                    "{ token: 'string', foreground: '" + ColorHex(ThemeManager.GetColor("Syntax.String")).Substring(1) + "' }," +
                    "{ token: 'keyword', foreground: '" + ColorHex(ThemeManager.GetColor("Syntax.Keyword")).Substring(1) + "' }," +
                    "{ token: 'number', foreground: '" + ColorHex(ThemeManager.GetColor("Syntax.Number")).Substring(1) + "' }" +
                    "], colors: { 'editor.background': '" + bg + "', 'editor.foreground': '" + fg + "', " +
                    "'editor.lineHighlightBackground': '" + line + "', 'editorLineNumber.foreground': '" + gutter + "', " +
                    "'editorGutter.background': '" + bg + "' } });" +
                    "monaco.editor.setTheme('casium');";
                await MonacoView.ExecuteScriptAsync(js);
            }
            catch { }
        }

        private static string ColorHex(Color c)
        {
            return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
        }

        private static Brush BrushForCategory(string category)
        {
            switch (category)
            {
                case "ok": return GreenBrush;
                case "err": return RedBrush;
                case "warn": return YellowBrush;
                case "exec": return WhiteBrush;
                case "cmd": return PurpleBrush;
                default: return GrayBrush;
            }
        }

        private async void SettingToggle_Changed(object sender, RoutedEventArgs e)
        {
            var check = (CheckBox)sender;
            string key = (string)check.Tag;
            bool on = check.IsChecked == true;

            switch (key)
            {
                case "alwaysontop":
                    Topmost = on;
                    break;
                case "linenumbers":
                    if (_useMonaco)
                    {
                        break;
                    }
                    GutterColumn.Width = on ? new GridLength(52) : new GridLength(0);
                    LineNumbers.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case "autoattach":
                    _autoAttach = on;
                    if (on && !_attached)
                    {
                        await AttachSequence();
                    }
                    break;
                case "timestamps":
                    _showTimestamps = on;
                    break;
            }
        }

        private void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            new LoginWindow().Show();
            Close();
        }

    }
}
