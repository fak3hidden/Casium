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
        private QuorumBridge _api;
        private bool _attaching;
        private bool _monacoReady;
        private bool _useMonaco;
        private readonly QuorumEditorHost _quorum = new QuorumEditorHost();
        private bool _useQuorum;
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

            if (!InitQuorum())
            {
                _ = InitMonacoAsync();
            }
            InitQuorumApi();

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

        // ---------- QuorumMonaco (WinForms control, preferred when the DLL is present) -------

        private bool InitQuorum()
        {
            if (!_quorum.TryCreate())
            {
                if (!string.IsNullOrEmpty(_quorum.LastError) && _quorum.LastError != "QuorumMonaco.dll not found")
                {
                    AddLog("warn", "QuorumMonaco could not start: " + _quorum.LastError);
                }
                return false;
            }
            EditorSurface.Children.Add(_quorum.Element);
            EditorScroll.Visibility = Visibility.Collapsed;
            MonacoView.Visibility = Visibility.Collapsed;
            _useQuorum = true;
            _useMonaco = true;   // "external editor" flag for the rest of the code
            AddLog("ok", "QuorumMonaco editor ready.");
            return true;
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

        // ---------- tab strip scrolling -------------------------------------------------
        // The strip is a clipped panel moved with an animated TranslateTransform, which gives
        // buttery, GPU-composited motion (a ScrollViewer + timer looked steppy).

        private double _tabScrollTarget;

        private double TabScrollMax
        {
            get { return Math.Max(0, TabStripPanel.ActualWidth - TabStripScroll.ActualWidth); }
        }

        private double TabScrollCurrent
        {
            get { return -TabStripOffset.X; }
        }

        private void SmoothScrollTabsTo(double target, bool animate = true)
        {
            _tabScrollTarget = Math.Max(0, Math.Min(TabScrollMax, target));
            if (!animate)
            {
                TabStripOffset.BeginAnimation(TranslateTransform.XProperty, null);
                TabStripOffset.X = -_tabScrollTarget;
                UpdateTabScrollButtons();
                return;
            }
            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                To = -_tabScrollTarget,
                Duration = TimeSpan.FromMilliseconds(260),
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                }
            };
            anim.Completed += (s2, e2) => UpdateTabScrollButtons();
            TabStripOffset.BeginAnimation(TranslateTransform.XProperty, anim,
                System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
            UpdateTabScrollButtons(_tabScrollTarget);
        }

        private void TabStripScroll_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (TabScrollMax <= 0)
            {
                return;
            }
            SmoothScrollTabsTo(_tabScrollTarget - e.Delta * 0.8);
            e.Handled = true;
        }

        private void TabStripScroll_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // clamp when the strip grows/shrinks (tab closed, window resized)
            if (_tabScrollTarget > TabScrollMax)
            {
                SmoothScrollTabsTo(TabScrollMax);
            }
            UpdateTabScrollButtons();
        }

        private void TabScrollLeft_Click(object sender, RoutedEventArgs e)
        {
            SmoothScrollTabsTo(_tabScrollTarget - 180);
        }

        private void TabScrollRight_Click(object sender, RoutedEventArgs e)
        {
            SmoothScrollTabsTo(_tabScrollTarget + 180);
        }

        private void UpdateTabScrollButtons(double? offset = null)
        {
            if (TabStripScroll == null || TabScrollLeft == null || TabScrollRight == null)
            {
                return;
            }
            double max = TabScrollMax;
            double off = offset ?? TabScrollCurrent;
            bool overflow = max > 0.5;
            TabScrollLeft.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
            TabScrollRight.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
            TabScrollLeft.IsEnabled = off > 0.5;
            TabScrollRight.IsEnabled = off < max - 0.5;
        }

        private void ScrollActiveTabIntoView()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (FrameworkElement child in TabStripPanel.Children.OfType<FrameworkElement>())
                {
                    if (NavState.GetIsActive(child))
                    {
                        double left = 0;
                        foreach (FrameworkElement c in TabStripPanel.Children.OfType<FrameworkElement>())
                        {
                            if (c == child) break;
                            left += c.ActualWidth + c.Margin.Left + c.Margin.Right;
                        }
                        double right = left + child.ActualWidth + child.Margin.Left + child.Margin.Right;
                        double view = TabStripScroll.ActualWidth;
                        if (left < _tabScrollTarget)
                        {
                            SmoothScrollTabsTo(left);
                        }
                        else if (right > _tabScrollTarget + view)
                        {
                            SmoothScrollTabsTo(right - view);
                        }
                        break;
                    }
                }
                UpdateTabScrollButtons();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private async Task<string> GetMonacoCodeAsync()
        {
            if (_useQuorum)
            {
                return await _quorum.GetTextAsync();
            }
            string raw = await MonacoView.ExecuteScriptAsync("window.Bubble.getCode()");
            return DecodeJsString(raw);
        }

        private Task SetMonacoCodeAsync(string code)
        {
            if (_useQuorum)
            {
                return _quorum.SetTextAsync(code);
            }
            return MonacoView.ExecuteScriptAsync(
                "window.Bubble.setCode(" + EncodeJsString(code ?? string.Empty) + ")");
        }

        private async Task SyncMonacoToTabAsync()
        {
            if (!_monacoReady && !_useQuorum)
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
                if (code == null || tab != _selectedTab)
                {
                    return;
                }
                tab.Content = code;
            }
            catch { }
        }

        private async Task SyncTabToMonacoAsync()
        {
            if ((!_monacoReady && !_useQuorum) || _selectedTab == null)
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
            if (!_monacoReady || _useQuorum || _currentView != "ExecutorView")
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

        private readonly HashSet<string> _expandedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _renamingPath;

        private static readonly Geometry FileGlyph = Geometry.Parse("M6,2 H14 L20,8 V22 H6 Z M14,2 V8 H20");
        private static readonly Geometry FolderGlyph = Geometry.Parse("M3,7 V19 A1,1 0 0,0 4,20 H20 A1,1 0 0,0 21,19 V9 A1,1 0 0,0 20,8 H12 L10,6 H4 A1,1 0 0,0 3,7 Z");
        private static readonly Geometry FolderPlusGlyph = Geometry.Parse("M3,7 V19 A1,1 0 0,0 4,20 H20 A1,1 0 0,0 21,19 V9 A1,1 0 0,0 20,8 H12 L10,6 H4 A1,1 0 0,0 3,7 Z M12,12 V17 M9.5,14.5 H14.5");
        private static readonly Geometry PlusGlyph = Geometry.Parse("M12,5 V19 M5,12 H19");
        private static readonly Geometry PlayGlyph = Geometry.Parse("M8,5 L19,12 L8,19 Z");
        private static readonly Geometry CopyGlyph = Geometry.Parse("M9,9 H20 V20 H9 Z M5,15 H4 V4 H15 V5");
        private static readonly Geometry PencilGlyph = Geometry.Parse("M4,20 H8 L19,9 L15,5 L4,16 Z M13,7 L17,11");
        private static readonly Geometry TrashGlyph = Geometry.Parse("M4,7 H20 M9,7 V4 H15 V7 M6,7 L7,20 H17 L18,7 M10,11 V17 M14,11 V17");
        private static readonly Geometry ChevronGlyph = Geometry.Parse("M9,6 L15,12 L9,18");

        private static bool IsScriptFile(string f)
        {
            return f.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".luau", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
        }

        private void FillExplorer(StackPanel panel, string dir)
        {
            panel.Children.Clear();
            string filter = (FilterBox.Text ?? string.Empty).Trim().ToLowerInvariant();
            FillDirectory(panel, dir, 0, filter);

            if (panel.Children.Count == 0)
            {
                var empty = new TextBlock { Text = filter.Length > 0 ? "No matches" : "Empty", FontSize = 12, Margin = new Thickness(10, 3, 0, 3) };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
                panel.Children.Add(empty);
            }
        }

        private void FillDirectory(StackPanel panel, string dir, int depth, string filter)
        {
            string[] dirs, files;
            try
            {
                dirs = Directory.GetDirectories(dir).OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase).ToArray();
                files = Directory.GetFiles(dir).Where(IsScriptFile).OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch
            {
                return;
            }

            foreach (var sub in dirs)
            {
                bool expanded = _expandedDirs.Contains(sub) || filter.Length > 0;
                var children = new StackPanel();
                FillDirectory(children, sub, depth + 1, filter);
                if (filter.Length > 0 && children.Children.Count == 0)
                {
                    continue;
                }
                panel.Children.Add(MakeFolderRow(sub, depth, expanded));
                if (expanded)
                {
                    panel.Children.Add(children);
                }
            }

            foreach (var file in files)
            {
                string name = Path.GetFileName(file);
                if (filter.Length > 0 && !name.ToLowerInvariant().Contains(filter))
                {
                    continue;
                }
                panel.Children.Add(MakeFileRow(name, file, depth));
            }
        }

        private static System.Windows.Shapes.Path Glyph(Geometry data, double size, string brushKey, double thickness = 1.6)
        {
            var p = new System.Windows.Shapes.Path
            {
                Data = data, StrokeThickness = thickness, Width = size, Height = size,
                Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center
            };
            p.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, brushKey);
            return p;
        }

        private Button IconButton(Geometry data, string tip, RoutedEventHandler onClick, object tag)
        {
            var b = new Button { Style = (Style)FindResource("Button.Icon"), Width = 22, Height = 22, ToolTip = tip, Tag = tag };
            var g = new System.Windows.Shapes.Path { Data = data, StrokeThickness = 1.6, Width = 11, Height = 11, Stretch = Stretch.Uniform };
            g.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { Source = b });
            b.Content = g;
            b.Click += onClick;
            return b;
        }

        private FrameworkElement MakeFolderRow(string path, int depth, bool expanded)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var chevron = Glyph(ChevronGlyph, 9, "Text.Tertiary", 1.8);
            chevron.RenderTransformOrigin = new Point(0.5, 0.5);
            chevron.RenderTransform = new RotateTransform(expanded ? 90 : 0);
            row.Children.Add(chevron);
            row.Children.Add(Glyph(FolderGlyph, 13, "Text.Secondary"));
            row.Children.Add(MakeLabel(path, Path.GetFileName(path)));

            var btn = new Button
            {
                Style = (Style)FindResource("ExplorerRow"),
                Content = row, Tag = path,
                Padding = new Thickness(8 + depth * 14, 0, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            btn.Click += FolderRow_Click;
            btn.ContextMenu = BuildFolderMenu(path);
            if (string.Equals(_renamingPath, path, StringComparison.OrdinalIgnoreCase))
            {
                btn.Height = 30;
            }
            Grid.SetColumnSpan(btn, 2);
            grid.Children.Add(btn);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
            actions.Children.Add(IconButton(PlusGlyph, "New script", NewFileInFolder_Click, path));
            actions.Children.Add(IconButton(FolderPlusGlyph, "New folder", NewFolder_Click, path));
            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);
            grid.MouseEnter += (s, e) => actions.Opacity = 1;
            grid.MouseLeave += (s, e) => actions.Opacity = 0;

            return grid;
        }

        private Button MakeFileRow(string name, string path, int depth = 0, bool allowRename = true)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Glyph(FileGlyph, 12, "Text.Secondary"));
            row.Children.Add(MakeLabel(path, name, allowRename));

            var btn = new Button
            {
                Style = (Style)FindResource("ExplorerRow"),
                Content = row, Tag = path, ToolTip = path,
                Padding = new Thickness(depth > 0 ? 8 + depth * 14 + 17 : 8, 0, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            btn.Click += ExplorerFile_Click;
            btn.ContextMenu = BuildFileMenu(path);
            if (allowRename && string.Equals(_renamingPath, path, StringComparison.OrdinalIgnoreCase))
            {
                btn.Height = 30;
            }
            return btn;
        }

        private FrameworkElement MakeLabel(string path, string name, bool allowRename = true)
        {
            if (allowRename && string.Equals(_renamingPath, path, StringComparison.OrdinalIgnoreCase))
            {
                var box = new TextBox
                {
                    Text = name, Tag = path,
                    Style = (Style)FindResource("TextBox.Field"),
                    Padding = new Thickness(6, 0, 6, 0), Height = 24, MinWidth = 130,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0), FontSize = 12.5
                };
                box.KeyDown += RenameBox_KeyDown;
                box.LostFocus += RenameBox_LostFocus;
                box.Loaded += (s, e) =>
                {
                    box.Focus();
                    int dot = name.LastIndexOf('.');
                    box.Select(0, dot > 0 && File.Exists(path) ? dot : name.Length);
                };
                return box;
            }
            return new TextBlock { Text = name, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        }

        private MenuItem MenuEntry(string header, Geometry icon, RoutedEventHandler onClick, string path, bool danger = false)
        {
            var mi = new MenuItem { Header = header, Tag = path, Style = (Style)FindResource(danger ? "Menu.Danger" : "Menu.Item") };
            var g = new System.Windows.Shapes.Path { Data = icon, StrokeThickness = 1.6, Stretch = Stretch.Uniform, Width = 13, Height = 13 };
            g.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { Source = mi });
            mi.Icon = g;
            mi.Click += onClick;
            return mi;
        }

        private ContextMenu BuildFileMenu(string path)
        {
            var menu = new ContextMenu { Style = (Style)FindResource("Menu.Popup") };
            menu.Items.Add(MenuEntry("Open", FileGlyph, Menu_Open, path));
            menu.Items.Add(MenuEntry("Execute", PlayGlyph, Menu_Execute, path));
            menu.Items.Add(MenuEntry("Duplicate", CopyGlyph, Menu_Duplicate, path));
            menu.Items.Add(MenuEntry("Rename", PencilGlyph, Menu_Rename, path));
            menu.Items.Add(MenuEntry("Show in Explorer", FolderGlyph, Menu_ShowInExplorer, path));
            menu.Items.Add(new Separator { Style = (Style)FindResource("Menu.Separator") });
            menu.Items.Add(MenuEntry("Delete", TrashGlyph, Menu_Delete, path, danger: true));
            return menu;
        }

        private ContextMenu BuildFolderMenu(string path)
        {
            var menu = new ContextMenu { Style = (Style)FindResource("Menu.Popup") };
            menu.Items.Add(MenuEntry("New script", PlusGlyph, NewFileInFolder_Click, path));
            menu.Items.Add(MenuEntry("New folder", FolderPlusGlyph, NewFolder_Click, path));
            menu.Items.Add(new Separator { Style = (Style)FindResource("Menu.Separator") });
            menu.Items.Add(MenuEntry("Rename", PencilGlyph, Menu_Rename, path));
            menu.Items.Add(MenuEntry("Show in Explorer", FolderGlyph, Menu_ShowInExplorer, path));
            menu.Items.Add(new Separator { Style = (Style)FindResource("Menu.Separator") });
            menu.Items.Add(MenuEntry("Delete", TrashGlyph, Menu_Delete, path, danger: true));
            return menu;
        }

        private static string PathOf(object sender)
        {
            return (string)((FrameworkElement)sender).Tag;
        }

        private void FolderRow_Click(object sender, RoutedEventArgs e)
        {
            string path = PathOf(sender);
            if (!_expandedDirs.Remove(path))
            {
                _expandedDirs.Add(path);
            }
            RefreshExplorer();
        }

        private async void Menu_Open(object sender, RoutedEventArgs e)
        {
            await OpenPath(PathOf(sender));
        }

        private async void Menu_Execute(object sender, RoutedEventArgs e)
        {
            await OpenPath(PathOf(sender));
            await ExecuteCurrentTab();
        }

        private void Menu_Duplicate(object sender, RoutedEventArgs e)
        {
            string src = PathOf(sender);
            try
            {
                string dir = Path.GetDirectoryName(src);
                string stem = Path.GetFileNameWithoutExtension(src);
                string ext = Path.GetExtension(src);
                string dst = Path.Combine(dir, stem + " copy" + ext);
                int n = 2;
                while (File.Exists(dst))
                {
                    dst = Path.Combine(dir, string.Format("{0} copy {1}{2}", stem, n++, ext));
                }
                File.Copy(src, dst);
                RefreshExplorer();
            }
            catch (Exception ex)
            {
                AddLog("err", "Could not duplicate: " + ex.Message);
            }
        }

        private void Menu_Rename(object sender, RoutedEventArgs e)
        {
            _renamingPath = PathOf(sender);
            RefreshExplorer();
        }

        private void Menu_ShowInExplorer(object sender, RoutedEventArgs e)
        {
            string path = PathOf(sender);
            try
            {
                if (Directory.Exists(path))
                {
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\"");
                }
                else
                {
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
                }
            }
            catch (Exception ex)
            {
                AddLog("err", "Could not open Explorer: " + ex.Message);
            }
        }

        private async void Menu_Delete(object sender, RoutedEventArgs e)
        {
            string path = PathOf(sender);
            string name = Path.GetFileName(path);
            bool isDir = Directory.Exists(path);
            var result = MessageBox.Show(this,
                string.Format("Delete {0} '{1}'?{2}", isDir ? "folder" : "file", name, isDir ? " Everything inside will be removed." : string.Empty),
                "Casium", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }
            try
            {
                if (isDir)
                {
                    Directory.Delete(path, true);
                    foreach (var t in _tabs.Where(t => t.FilePath != null && t.FilePath.StartsWith(path, StringComparison.OrdinalIgnoreCase)).ToList())
                    {
                        await CloseTabAsync(t);
                    }
                }
                else
                {
                    File.Delete(path);
                    var open = _tabs.FirstOrDefault(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
                    if (open != null)
                    {
                        await CloseTabAsync(open);
                    }
                }
                _recent.RemoveAll(p => p.StartsWith(path, StringComparison.OrdinalIgnoreCase));
                RenderRecent();
                RefreshExplorer();
                AddLog("sys", string.Format("Deleted '{0}'.", name));
            }
            catch (Exception ex)
            {
                AddLog("err", "Could not delete: " + ex.Message);
            }
        }

        private void RenameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitRename((TextBox)sender);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _renamingPath = null;
                RefreshExplorer();
                e.Handled = true;
            }
        }

        private void RenameBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_renamingPath != null)
            {
                CommitRename((TextBox)sender);
            }
        }

        private void CommitRename(TextBox box)
        {
            string oldPath = (string)box.Tag;
            string newName = box.Text.Trim();
            _renamingPath = null;

            if (newName.Length == 0 || newName == Path.GetFileName(oldPath)
                || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                RefreshExplorer();
                return;
            }

            try
            {
                string newPath = Path.Combine(Path.GetDirectoryName(oldPath), newName);
                if (Directory.Exists(oldPath))
                {
                    Directory.Move(oldPath, newPath);
                    if (_expandedDirs.Remove(oldPath)) _expandedDirs.Add(newPath);
                    foreach (var t in _tabs.Where(t => t.FilePath != null && t.FilePath.StartsWith(oldPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        t.FilePath = newPath + t.FilePath.Substring(oldPath.Length);
                    }
                }
                else
                {
                    if (!Path.HasExtension(newName)) newPath += Path.GetExtension(oldPath);
                    File.Move(oldPath, newPath);
                    var tab = _tabs.FirstOrDefault(t => string.Equals(t.FilePath, oldPath, StringComparison.OrdinalIgnoreCase));
                    if (tab != null)
                    {
                        tab.FilePath = newPath;
                        tab.Title = Path.GetFileName(newPath);
                    }
                    int ri = _recent.FindIndex(p => string.Equals(p, oldPath, StringComparison.OrdinalIgnoreCase));
                    if (ri >= 0) _recent[ri] = newPath;
                }
                RenderRecent();
                RefreshTabStrip();
                UpdateBreadcrumb();
            }
            catch (Exception ex)
            {
                AddLog("err", "Could not rename: " + ex.Message);
            }
            RefreshExplorer();
        }

        private async void ExplorerFile_Click(object sender, RoutedEventArgs e)
        {
            if (_renamingPath != null)
            {
                return;
            }
            await OpenPath(PathOf(sender));
        }

        // ---------- breadcrumb ---------------------------------------------------------

        private void UpdateBreadcrumb()
        {
            BreadcrumbPanel.Children.Clear();
            if (_selectedTab == null)
            {
                return;
            }

            var parts = new List<string>();
            string path = _selectedTab.FilePath;
            if (!string.IsNullOrEmpty(path))
            {
                string root = path.StartsWith(AutoExecDir, StringComparison.OrdinalIgnoreCase) ? AutoExecDir
                            : path.StartsWith(ScriptsDir, StringComparison.OrdinalIgnoreCase) ? ScriptsDir : null;
                if (root != null)
                {
                    parts.Add(root == AutoExecDir ? "Auto Execute" : "Scripts");
                    string rel = path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar);
                    parts.AddRange(rel.Split(Path.DirectorySeparatorChar));
                }
                else
                {
                    parts.Add(Path.GetFileName(path));
                }
            }
            else
            {
                parts.Add(_selectedTab.Title);
            }

            for (int i = 0; i < parts.Count; i++)
            {
                bool last = i == parts.Count - 1;
                if (last)
                {
                    BreadcrumbPanel.Children.Add(Glyph(FileGlyph, 12, "Text.Secondary"));
                }
                var t = new TextBlock { Text = parts[i], FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(last ? 6 : 0, 0, 0, 0) };
                t.SetResourceReference(TextBlock.ForegroundProperty, last ? "Text.Primary" : "Text.Secondary");
                BreadcrumbPanel.Children.Add(t);
                if (!last)
                {
                    var sep = Glyph(ChevronGlyph, 8, "Text.Tertiary", 1.8);
                    sep.Margin = new Thickness(8, 0, 8, 0);
                    BreadcrumbPanel.Children.Add(sep);
                }
            }
        }

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            FilterHint.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshExplorer();
        }

        private string FolderFor(object sender)
        {
            string tag = (string)((FrameworkElement)sender).Tag;
            if (tag == "autoexec") return AutoExecDir;
            if (tag == "scripts") return ScriptsDir;
            return tag;
        }

        private void RevealFolder(string dir)
        {
            string d = dir;
            while (d != null && !string.Equals(d, ScriptsDir, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(d, AutoExecDir, StringComparison.OrdinalIgnoreCase))
            {
                _expandedDirs.Add(d);
                d = Path.GetDirectoryName(d);
            }
            if (dir.StartsWith(AutoExecDir, StringComparison.OrdinalIgnoreCase))
            {
                AutoExecHeader.IsChecked = true;
                AutoExecList.Visibility = Visibility.Visible;
            }
            else
            {
                ScriptsHeader.IsChecked = true;
                ScriptsList.Visibility = Visibility.Visible;
            }
        }

        private async void NewFileInFolder_Click(object sender, RoutedEventArgs e)
        {
            string dir = FolderFor(sender);
            string path = null;
            for (int i = 1; i < 1000; i++)
            {
                string candidate = Path.Combine(dir, i == 1 ? "Untitled.lua" : string.Format("Untitled {0}.lua", i));
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
            RevealFolder(dir);
            _renamingPath = path;
            RefreshExplorer();
            await OpenPath(path);
        }

        private void NewFolder_Click(object sender, RoutedEventArgs e)
        {
            string parent = FolderFor(sender);
            string path = null;
            for (int i = 1; i < 1000; i++)
            {
                string candidate = Path.Combine(parent, i == 1 ? "New folder" : string.Format("New folder {0}", i));
                if (!Directory.Exists(candidate))
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
                Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                AddLog("err", "Could not create folder: " + ex.Message);
                return;
            }
            RevealFolder(parent);
            _expandedDirs.Add(path);
            _renamingPath = path;
            RefreshExplorer();
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
                var b = MakeFileRow(Path.GetFileName(path), path, 0, allowRename: false);
                b.ContextMenu = null;
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
            UpdateBreadcrumb();
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
            ScrollActiveTabIntoView();
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
            if (!_outputOnly || category == "exec" || category == "ok" || category == "err" || category == "out")
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
                if (!_outputOnly || entry.Category == "exec" || entry.Category == "ok" || entry.Category == "err" || entry.Category == "out")
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

        private void InitQuorumApi()
        {
            _api = new QuorumBridge(Dispatcher, AddLog);
            if (_api.Init(ScriptsDir, AutoExecDir))
            {
                AddLog("sys", "Quorum API ready.");
            }
            else
            {
                _api = null;
            }
        }

        private void RefreshAttachState()
        {
            bool on = _api != null && _api.IsAttached();
            if (on != _attached)
            {
                _attached = on;
                SetAttachedUi(on);
            }
        }

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
                if (_api == null)
                {
                    AddLog("err", "Quorum API is not available (QuorumAPI.dll missing?).");
                    return;
                }
                string result = await _api.AttachAsync();
                bool ok = result == "Attached" || result == "Attaching" || _api.IsAttached();
                if (ok)
                {
                    AddLog("ok", "Attached.");
                    _attached = true;
                    SetAttachedUi(true);
                }
                else
                {
                    AddLog("err", "Attach failed: " + result);
                    _attached = false;
                    SetAttachedUi(false);
                }
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
            RefreshAttachState();
            if (!_attached)
            {
                AddLog("err", "No client attached. Press Attach first.");
                return;
            }
            AddLog("exec", string.Format("Executing '{0}'...", _selectedTab.Title));
            if (_api != null && _api.Execute(_selectedTab.Content))
            {
                AddLog("ok", "Executed.");
            }
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
                UpdateBreadcrumb();
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
            if (_useQuorum)
            {
                _quorum.Refresh();
                return;
            }
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
                case "out": return WhiteBrush;
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
                    if (_api != null)
                    {
                        _api.SetAutoAttach(on);
                    }
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
