using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

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

        private class HubScript
        {
            public string Name { get; set; }
            public string Author { get; set; }
            public string Badge { get; set; }
            public string Key { get; set; }
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

        private static readonly Random Rnd = new Random();
        private static readonly SolidColorBrush GrayBrush = new SolidColorBrush(Color.FromRgb(0xA7, 0x9F, 0xBF));
        private static readonly SolidColorBrush WhiteBrush = new SolidColorBrush(Color.FromRgb(0xED, 0xE9, 0xFE));
        private static readonly SolidColorBrush GreenBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
        private static readonly SolidColorBrush RedBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
        private static readonly SolidColorBrush YellowBrush = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
        private static readonly SolidColorBrush PurpleBrush = new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA));

        private readonly List<EditorTab> _tabs = new List<EditorTab>();
        private EditorTab _selectedTab;
        private int _tabCounter;
        private bool _syncingEditor;
        private bool _highlighting;

        private readonly List<LogEntry> _allLogs = new List<LogEntry>();
        private readonly ObservableCollection<LogEntry> _visibleLogs = new ObservableCollection<LogEntry>();
        private bool _outputOnly;
        private bool _showTimestamps = true;

        private readonly List<HubScript> _hubScripts = new List<HubScript>();
        private readonly Dictionary<string, string> _templates = new Dictionary<string, string>();

        private readonly Dictionary<string, TextBlock> _infoValues = new Dictionary<string, TextBlock>();
        private readonly Dictionary<string, TextBlock> _execValues = new Dictionary<string, TextBlock>();

        private readonly Dictionary<string, FrameworkElement> _views = new Dictionary<string, FrameworkElement>();
        private readonly List<Button> _navButtons = new List<Button>();

        private readonly DispatcherTimer _fpsTimer = new DispatcherTimer();
        private readonly DateTime _appStart = DateTime.Now;
        private DateTime _attachTime;

        private bool _attached;
        private bool _attaching;
        private bool _autoAttach = true;
        private string _username = "—";
        private string _accessKey;
        private string _lastSaved = "Never";

        private static readonly Regex LuaToken = new Regex(
            @"(?<comment>--\[\[.*?\]\]|--[^\n]*)" +
            @"|(?<string>""([^""\\\n]|\\.)*""|'([^'\\\n]|\\.)*')" +
            @"|(?<keyword>\b(local|function|end|if|then|else|elseif|for|while|do|return|break|in|repeat|until|nil|true|false|and|or|not)\b)" +
            @"|(?<number>\b\d+(\.\d+)?\b)" +
            @"|(?<builtin>\b(game|workspace|script|print|pairs|ipairs|task|wait|tostring|tonumber|require|loadstring|Instance|Vector3|CFrame|Enum|typeof|select|unpack|pcall|tick|os|math|string|table)\b)",
            RegexOptions.Compiled);

        private static readonly SolidColorBrush LuaDefault = new SolidColorBrush(Color.FromRgb(0xE6, 0xE3, 0xF0));
        private static readonly SolidColorBrush LuaComment = new SolidColorBrush(Color.FromRgb(0x6E, 0x65, 0x87));
        private static readonly SolidColorBrush LuaString = new SolidColorBrush(Color.FromRgb(0x9E, 0xCE, 0x6A));
        private static readonly SolidColorBrush LuaKeyword = new SolidColorBrush(Color.FromRgb(0xBB, 0x9A, 0xF7));
        private static readonly SolidColorBrush LuaNumber = new SolidColorBrush(Color.FromRgb(0xFF, 0x9E, 0x64));
        private static readonly SolidColorBrush LuaBuiltin = new SolidColorBrush(Color.FromRgb(0x7A, 0xA2, 0xF7));

        private const string DefaultScript =
@"-- Casium Executor
local Players = game:GetService(""Players"")
local LocalPlayer = Players.LocalPlayer

local function hi()
    print(""Hello from Casium!"")
end

hi()

-- Example
for i = 1, 5 do
    task.wait(1)
    print(""Count: "" .. i)
end

-- Made with power.";

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
            TopUserText.Text = _username;
            NavUserText.Text = _username;
            AboutUserText.Text = string.Format("Signed in as {0}", _username);

            BuildViews();
            BuildInfoPanels();
            BuildHubData();
            BuildSettings();
            InitNetwork();
            ResetKey(silent: true);

            EditorScroll.SizeChanged += (ss, ee) => RefreshPageWidth();

            LogList.ItemsSource = _visibleLogs;
            MiniHubList.ItemsSource = new List<HubScript>(_hubScripts);

            NewTab("Script1", DefaultScript);
            SwitchView("ExecutorView");

            AddLog("sys", "Welcome to Casium Executor.");
            AddLog("sys", string.Format("Signed in as {0}.", _username));

            _fpsTimer.Interval = TimeSpan.FromSeconds(1);
            _fpsTimer.Tick += FpsTimer_Tick;
            _fpsTimer.Start();

            if (_autoAttach)
            {
                await AttachSequence();
            }
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
            _views["ExecutorView"] = ExecutorView;
            _views["HubView"] = HubView;
            _views["NetworkView"] = NetworkView;
            _views["PlayerView"] = PlayerView;
            _views["SettingsView"] = SettingsView;
            _views["KeyView"] = KeyView;
            _views["AboutView"] = AboutView;

            _navButtons.AddRange(new[] { NavExecutor, NavHub, NavNetwork, NavPlayer, NavSettings, NavKey, NavAbout });
        }

        private void NavButton_Click(object sender, RoutedEventArgs e)
        {
            SwitchView((string)((Button)sender).Tag);
        }

        private void SwitchView(string key)
        {
            foreach (var pair in _views)
            {
                pair.Value.Visibility = pair.Key == key ? Visibility.Visible : Visibility.Collapsed;
            }

            var gradient = (Brush)FindResource("AccentGradient");
            foreach (var btn in _navButtons)
            {
                bool active = (string)btn.Tag == key;
                btn.Background = active ? gradient : Brushes.Transparent;
                btn.Foreground = active ? Brushes.White : (Brush)FindResource("MutedBrush");
            }

            if (key == "ExecutorView" && _selectedTab != null)
            {
                EditorBox.Focus();
            }
        }

        // ---------- editor tabs -------------------------------------------------------

        private void NewTab(string title, string content, string path = null)
        {
            var tab = new EditorTab { Title = title, Content = content ?? string.Empty, FilePath = path };
            _tabs.Add(tab);
            SelectTab(tab);
        }

        private void SelectTab(EditorTab tab)
        {
            if (tab == null || !_tabs.Contains(tab))
            {
                return;
            }
            _selectedTab = tab;
            LoadTabContent();
            RefreshTabStrip();
            UpdateScriptInfo();
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

        private void RefreshTabStrip()
        {
            TabStripPanel.Children.Clear();
            var gradient = (Brush)FindResource("AccentGradient");

            foreach (var tab in _tabs)
            {
                bool active = tab == _selectedTab;
                var border = new Border
                {
                    Background = active ? gradient : new SolidColorBrush(Color.FromRgb(0x17, 0x13, 0x24)),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12, 7, 6, 7),
                    Margin = new Thickness(0, 0, 6, 0),
                    Cursor = Cursors.Hand,
                    Tag = tab
                };
                border.MouseLeftButtonDown += SelectTab_Click;

                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new TextBlock
                {
                    Text = tab.Title + (tab.IsDirty ? " •" : string.Empty),
                    Foreground = active ? Brushes.White : GrayBrush,
                    FontSize = 12.5,
                    FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center
                });

                var close = new Button
                {
                    Content = "✕",
                    Tag = tab,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = active ? Brushes.White : GrayBrush,
                    FontSize = 10,
                    Padding = new Thickness(8, 0, 4, 0),
                    Cursor = Cursors.Hand
                };
                close.Click += CloseTab_Click;
                row.Children.Add(close);

                border.Child = row;
                TabStripPanel.Children.Add(border);
            }
        }

        private void SelectTab_Click(object sender, MouseButtonEventArgs e)
        {
            SelectTab((EditorTab)((Border)sender).Tag);
        }

        private void AddTabButton_Click(object sender, RoutedEventArgs e)
        {
            NewTab(string.Format("Script{0}", ++_tabCounter + 0), "-- New script\n");
            SwitchView("ExecutorView");
        }

        private void CloseTab_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var tab = (EditorTab)((Button)sender).Tag;
            int index = _tabs.IndexOf(tab);
            if (index < 0)
            {
                return;
            }
            _tabs.RemoveAt(index);
            if (_tabs.Count == 0)
            {
                NewTab(string.Format("Script{0}", ++_tabCounter), "-- New script\n");
            }
            else if (_selectedTab == tab)
            {
                SelectTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
            }
            else
            {
                RefreshTabStrip();
                UpdateScriptInfo();
            }
        }

        private void EditorOpenButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFile();
        }

        private void EditorSaveButton_Click(object sender, RoutedEventArgs e)
        {
            SaveCurrentTab();
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
            UpdateScriptInfo();
        }

        private static Paragraph BuildLuaParagraph(string code)
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0),
                LineHeight = 19,
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
            double contentWidth = Math.Max(viewport - 46 - 20, 120);
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

        // ---------- script info + execution panels ---------------------------------------

        private void AddInfoRow(StackPanel panel, Dictionary<string, TextBlock> store, string label)
        {
            var grid = new Grid { Margin = new Thickness(2, 0, 2, 5) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock { Text = label, Foreground = GrayBrush, FontSize = 12 });
            var value = new TextBlock { Foreground = WhiteBrush, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 110 };
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
            panel.Children.Add(grid);
            store[label] = value;
        }

        private void BuildInfoPanels()
        {
            foreach (var label in new[] { "Lines", "Characters", "Words", "Tabs", "Last Saved", "File" })
            {
                AddInfoRow(InfoPanel, _infoValues, label);
            }
            foreach (var label in new[] { "Status", "Runtime", "Environment", "Script Type", "Last Exec" })
            {
                AddInfoRow(ExecPanel, _execValues, label);
            }

            _execValues["Status"].Text = "Idle";
            _execValues["Runtime"].Text = "00:00:00";
            _execValues["Environment"].Text = "Roblox";
            _execValues["Script Type"].Text = "Lua";
            _execValues["Last Exec"].Text = "Never";
        }

        private void UpdateScriptInfo()
        {
            if (_selectedTab == null)
            {
                return;
            }
            string content = _selectedTab.Content ?? string.Empty;
            _infoValues["Lines"].Text = content.Length == 0 ? "1" : content.Split('\n').Length.ToString();
            _infoValues["Characters"].Text = content.Length.ToString();
            _infoValues["Words"].Text = content.Split(new[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries).Length.ToString();
            _infoValues["Tabs"].Text = _tabs.Count.ToString();
            _infoValues["Last Saved"].Text = _lastSaved;
            _infoValues["File"].Text = _selectedTab.Title;
        }

        private void SetExecStatus(string text, Brush color)
        {
            _execValues["Status"].Text = text;
            _execValues["Status"].Foreground = color;
            ExecReadyText.Text = text;
            ReadyDot.Fill = color;
        }

        // ---------- console ---------------------------------------------------------------

        private void AddLog(string category, string message)
        {
            Brush brush = GrayBrush;
            switch (category)
            {
                case "ok": brush = GreenBrush; break;
                case "err": brush = RedBrush; break;
                case "warn": brush = YellowBrush; break;
                case "exec": brush = WhiteBrush; break;
                case "cmd": brush = PurpleBrush; break;
            }

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
            ConsoleTabButton.Foreground = Brushes.White;
            ConsoleTabButton.FontWeight = FontWeights.SemiBold;
            OutputTabButton.Foreground = GrayBrush;
            OutputTabButton.FontWeight = FontWeights.Normal;
            RefreshLogView();
        }

        private void OutputTab_Click(object sender, RoutedEventArgs e)
        {
            _outputOnly = true;
            OutputTabButton.Foreground = Brushes.White;
            OutputTabButton.FontWeight = FontWeights.SemiBold;
            ConsoleTabButton.Foreground = GrayBrush;
            ConsoleTabButton.FontWeight = FontWeights.Normal;
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
            try
            {
                AddLog("sys", "Attaching to Roblox...");
                await Task.Delay(650);
                AddLog("ok", "Successfully attached!");
                AddLog("exec", "Ready to execute.");

                _attached = true;
                _attachTime = DateTime.Now;
                StatusDot.Fill = GreenBrush;
                AttachedText.Text = "Roblox Player";
                DetachButton.Visibility = Visibility.Visible;
                SetExecStatus("Ready", GreenBrush);
            }
            finally
            {
                _attaching = false;
            }
        }

        private void Detach()
        {
            if (!_attached)
            {
                return;
            }
            _attached = false;
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x71, 0x71, 0x7A));
            AttachedText.Text = "Not attached";
            DetachButton.Visibility = Visibility.Collapsed;
            SetExecStatus("Idle", GrayBrush);
            AddLog("sys", "Detached.");
        }

        private async void InjectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_attached)
            {
                AddLog("warn", "Already injected.");
                return;
            }
            await AttachSequence();
        }

        private void DetachButton_Click(object sender, RoutedEventArgs e)
        {
            Detach();
        }

        private async void ExecuteButton_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteCurrentTab();
        }

        private async Task ExecuteCurrentTab()
        {
            if (_selectedTab == null)
            {
                return;
            }
            if (!_attached)
            {
                AddLog("err", "Not attached. Press Inject first.");
                return;
            }

            SetExecStatus("Executing", YellowBrush);
            AddLog("exec", string.Format("Executing '{0}'...", _selectedTab.Title));
            await Task.Delay(500);
            AddLog("ok", string.Format("Executed in {0}ms.", Rnd.Next(40, 160)));
            _execValues["Last Exec"].Text = DateTime.Now.ToString("HH:mm:ss");
            SetExecStatus("Ready", GreenBrush);
        }

        private async void ClipboardButton_Click(object sender, RoutedEventArgs e)
        {
            string text;
            try
            {
                text = Clipboard.GetText();
            }
            catch
            {
                AddLog("err", "Could not read the clipboard.");
                return;
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                AddLog("warn", "Clipboard is empty.");
                return;
            }
            NewTab("Clipboard", text);
            SwitchView("ExecutorView");
            await ExecuteCurrentTab();
        }

        // ---------- open / save ---------------------------------------------------------------

        private void OpenFileButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFile();
        }

        private void OpenFile()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Lua files (*.lua)|*.lua|Text files (*.txt)|*.txt|All files (*.*)|*.*"
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }
            try
            {
                NewTab(Path.GetFileName(dialog.FileName), File.ReadAllText(dialog.FileName), dialog.FileName);
                SwitchView("ExecutorView");
                AddLog("ok", string.Format("Opened '{0}'.", Path.GetFileName(dialog.FileName)));
            }
            catch (Exception ex)
            {
                AddLog("err", "Could not open file: " + ex.Message);
            }
        }

        private void SaveCurrentTab()
        {
            if (_selectedTab == null)
            {
                return;
            }
            try
            {
                if (string.IsNullOrEmpty(_selectedTab.FilePath))
                {
                    var dialog = new SaveFileDialog
                    {
                        FileName = _selectedTab.Title + ".lua",
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
                _lastSaved = DateTime.Now.ToString("HH:mm:ss");
                RefreshTabStrip();
                UpdateScriptInfo();
                StatusTabText.Text = _selectedTab.Title;
                AddLog("ok", string.Format("Saved '{0}'.", _selectedTab.Title));
            }
            catch (Exception ex)
            {
                AddLog("err", "Could not save file: " + ex.Message);
            }
        }

        // ---------- script hub -------------------------------------------------------------------

        private void BuildHubData()
        {
            _hubScripts.AddRange(new[]
            {
                new HubScript { Name = "Infinite Yield", Author = "EdgeIY", Badge = "Popular", Key = "infinite" },
                new HubScript { Name = "Keyless Hub", Author = "whiz", Badge = "Popular", Key = "keyless" },
                new HubScript { Name = "Dex Explorer", Author = "Dex", Badge = "Featured", Key = "dex" },
                new HubScript { Name = "Remote Spy", Author = "Dark", Badge = "Featured", Key = "spy" },
                new HubScript { Name = "FE Bypass", Author = "Ox1", Badge = "New", Key = "bypass" },
                new HubScript { Name = "Speed Hub", Author = "Casium", Badge = "New", Key = "speed" }
            });

            _templates["infinite"] = "-- Infinite Yield (demo stub)\nprint(\"Infinite Yield loaded.\")\n";
            _templates["keyless"] = "-- Keyless Hub (demo stub)\nprint(\"Keyless Hub loaded.\")\n";
            _templates["dex"] = "-- Dex Explorer (demo stub)\nprint(\"Dex Explorer opened.\")\n";
            _templates["spy"] = "-- Remote Spy (demo stub)\nprint(\"Remote Spy listening...\")\n";
            _templates["bypass"] = "-- FE Bypass (demo stub)\nprint(\"FilteringEnabled bypassed (not really).\")\n";
            _templates["speed"] =
@"-- Speed Hub
local Players = game:GetService(""Players"")
local LocalPlayer = Players.LocalPlayer

LocalPlayer.Character.Humanoid.WalkSpeed = 32
print(""WalkSpeed set to 32."")";

            RefreshHubCards(string.Empty);
        }

        private void RefreshHubCards(string filter)
        {
            HubPanel.Children.Clear();
            foreach (var script in FilterScripts(filter))
            {
                var card = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x17, 0x13, 0x24)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x2B, 0x21, 0x40)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(14),
                    Width = 210,
                    Margin = new Thickness(0, 0, 12, 12)
                };

                var stack = new StackPanel();
                stack.Children.Add(new TextBlock
                {
                    Text = script.Name, Foreground = Brushes.White,
                    FontSize = 14, FontWeight = FontWeights.Bold
                });
                stack.Children.Add(new TextBlock
                {
                    Text = "by " + script.Author, Foreground = GrayBrush,
                    FontSize = 12, Margin = new Thickness(0, 2, 0, 8)
                });

                var badge = new Border
                {
                    BorderBrush = PurpleBrush, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 2, 8, 2),
                    HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12)
                };
                badge.Child = new TextBlock { Text = script.Badge, Foreground = PurpleBrush, FontSize = 11 };
                stack.Children.Add(badge);

                var get = new Button
                {
                    Content = "Get",
                    Tag = script,
                    Height = 34,
                    FontSize = 13
                };
                get.Style = (Style)FindResource("GradientButton");
                get.Click += GetScript_Click;
                stack.Children.Add(get);

                card.Child = stack;
                HubPanel.Children.Add(card);
            }
        }

        private IEnumerable<HubScript> FilterScripts(string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                return _hubScripts;
            }
            string f = filter.Trim().ToLowerInvariant();
            return _hubScripts.Where(s => s.Name.ToLowerInvariant().Contains(f)
                || s.Author.ToLowerInvariant().Contains(f));
        }

        private void GetScript_Click(object sender, RoutedEventArgs e)
        {
            LoadHubScript((HubScript)((Button)sender).Tag);
        }

        private void PlayScript_Click(object sender, RoutedEventArgs e)
        {
            var script = (HubScript)((Button)sender).DataContext;
            if (script != null)
            {
                LoadHubScript(script);
            }
        }

        private void LoadHubScript(HubScript script)
        {
            string content = _templates.ContainsKey(script.Key) ? _templates[script.Key] : "-- " + script.Name + "\n";
            NewTab(script.Name, content);
            SwitchView("ExecutorView");
            AddLog("ok", string.Format("Loaded '{0}' into the editor.", script.Name));
        }

        private void HubSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            HubSearchHint.Visibility = string.IsNullOrEmpty(HubSearch.Text)
                ? Visibility.Visible : Visibility.Collapsed;
            RefreshHubCards(HubSearch.Text);
        }

        private void MiniHubSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            MiniHubHint.Visibility = string.IsNullOrEmpty(MiniHubSearch.Text)
                ? Visibility.Visible : Visibility.Collapsed;
            MiniHubList.ItemsSource = new List<HubScript>(FilterScripts(MiniHubSearch.Text));
        }

        private void BrowseAllButton_Click(object sender, RoutedEventArgs e)
        {
            SwitchView("HubView");
        }

        // ---------- network view ---------------------------------------------------------------------

        private static readonly string[] Servers =
            { "Germany #4821", "Netherlands #1107", "USA East #9034", "Singapore #2210" };

        private void InitNetwork()
        {
            foreach (var label in new[] { "Ping", "Server", "Packet Loss", "Uptime" })
            {
                var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.Children.Add(new TextBlock { Text = label, Foreground = WhiteBrush, FontSize = 13 });
                var value = new TextBlock { Foreground = PurpleBrush, FontSize = 13, FontWeight = FontWeights.SemiBold };
                Grid.SetColumn(value, 1);
                grid.Children.Add(value);
                NetworkPanel.Children.Add(grid);
                _execValues["net_" + label] = value;
            }
            RefreshNetwork(silent: true);
        }

        private void RefreshNetwork(bool silent)
        {
            _execValues["net_Ping"].Text = Rnd.Next(24, 68) + " ms";
            _execValues["net_Server"].Text = Servers[Rnd.Next(Servers.Length)];
            _execValues["net_Packet Loss"].Text = Rnd.Next(0, 2) + "%";
            if (!silent)
            {
                AddLog("sys", "Network stats refreshed.");
            }
        }

        private void NetRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshNetwork(silent: false);
        }

        // ---------- player view -------------------------------------------------------------------------

        private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SpeedValue != null)
            {
                SpeedValue.Text = ((int)SpeedSlider.Value).ToString();
            }
        }

        private void JumpSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (JumpValue != null)
            {
                JumpValue.Text = ((int)JumpSlider.Value).ToString();
            }
        }

        private void ApplyPlayerButton_Click(object sender, RoutedEventArgs e)
        {
            AddLog("exec", string.Format("Applied WalkSpeed={0} JumpPower={1}.",
                (int)SpeedSlider.Value, (int)JumpSlider.Value));
        }

        private void ResetPlayerButton_Click(object sender, RoutedEventArgs e)
        {
            SpeedSlider.Value = 16;
            JumpSlider.Value = 50;
            AddLog("sys", "Player stats reset to defaults.");
        }

        // ---------- settings view --------------------------------------------------------------------------

        private void BuildSettings()
        {
            AddSetting("alwaysontop", "Always on Top", "Keep Casium above other windows.", true);
            AddSetting("linenumbers", "Line Numbers", "Show the editor gutter.", true);
            AddSetting("autoattach", "Auto Attach", "Attach automatically on startup.", true);
            AddSetting("timestamps", "Timestamps", "Prefix console lines with the time.", true);
            AddSetting("safemode", "Safe Mode", "Extra checks before executing.", true);
        }

        private void AddSetting(string key, string title, string desc, bool def)
        {
            var row = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x17, 0x13, 0x24)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2B, 0x21, 0x40)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 10)
            };

            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = title, Foreground = WhiteBrush, FontSize = 13.5 });
            text.Children.Add(new TextBlock
            {
                Text = desc, Foreground = GrayBrush, FontSize = 12,
                Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap
            });

            var check = new CheckBox
            {
                Content = text,
                Tag = key,
                IsChecked = def,
                Style = (Style)FindResource("ToggleCheckBox")
            };
            check.Checked += SettingToggle_Changed;
            check.Unchecked += SettingToggle_Changed;

            row.Child = check;
            SettingsPanel.Children.Add(row);
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
                    GutterColumn.Width = on ? new GridLength(46) : new GridLength(0);
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
                case "safemode":
                    AddLog("sys", "Safe Mode " + (on ? "enabled." : "disabled."));
                    break;
            }
        }

        // ---------- key system view ---------------------------------------------------------------------------

        private static string RandomHex(int length)
        {
            const string chars = "0123456789ABCDEF";
            return new string(Enumerable.Range(0, length).Select(_ => chars[Rnd.Next(chars.Length)]).ToArray());
        }

        private void ResetKey(bool silent)
        {
            _accessKey = string.Format("CASI-{0}-{1}-{2}",
                RandomHex(4), RandomHex(4), RandomHex(4));
            KeyBox.Text = _accessKey;
            HwidText.Text = "HWID: " + RandomHex(4) + "-" + RandomHex(4) + "-" + RandomHex(4);
            if (!silent)
            {
                AddLog("warn", "Key reset. The old key was invalidated.");
            }
        }

        private void CopyKeyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(_accessKey);
                AddLog("ok", "Access key copied to clipboard.");
            }
            catch
            {
                AddLog("err", "Could not access the clipboard.");
            }
        }

        private void ResetKeyButton_Click(object sender, RoutedEventArgs e)
        {
            ResetKey(silent: false);
        }

        // ---------- about view -------------------------------------------------------------------------------------

        private void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            AddLog("ok", "You are on the latest version (" + Version + ").");
        }

        private void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            new LoginWindow().Show();
            Close();
        }

        // ---------- status bar timer ----------------------------------------------------------------------------------

        private void FpsTimer_Tick(object sender, EventArgs e)
        {
            FpsText.Text = _attached ? string.Format("FPS: {0}", Rnd.Next(58, 64)) : "FPS: —";

            TimeSpan uptime = DateTime.Now - _appStart;
            _execValues["Runtime"].Text = string.Format("{0:00}:{1:00}:{2:00}",
                (int)uptime.TotalHours, uptime.Minutes, uptime.Seconds);

            if (_attached)
            {
                TimeSpan connected = DateTime.Now - _attachTime;
                if (_execValues.ContainsKey("net_Uptime"))
                {
                    _execValues["net_Uptime"].Text = string.Format("{0:00}:{1:00}:{2:00}",
                        (int)connected.TotalHours, connected.Minutes, connected.Seconds);
                }
            }
        }
    }
}
