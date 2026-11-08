using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Nexonic.Core;

namespace Nexonic.App;

public partial class MainWindow : Window
{
    readonly DownloadManager manager;
    string filter = "all";
    bool rebuilding;
    bool updatingToolbarOverflow;
    Button[] toolbarButtons = [];
    UIElement[] toolbarElements = [];

    // One geometry policy for Light, Dark, Slate and PaperGray.
    const double ToolbarButtonMinWidth = 56;
    const double ToolbarButtonMinHeight = 44;
    static readonly Thickness ToolbarButtonPadding = new(4, 2, 4, 2);
    static readonly Thickness ToolbarButtonMargin = new(1, 0, 1, 0);

    static readonly FontFamily CategoryIconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static string IconFor(string key)
    {
        var category = key.StartsWith("cat:") ? key[4..] : key.StartsWith("ucat:") || key.StartsWith("fcat:") ? key[5..] : "";
        return category switch
        {
            "Video" => "\uE714", "Music" => "\uE8D6", "Documents" => "\uE8A5",
            "Programs" => "\uE8FC", "Compressed" => "\uE7B8", "Images" => "\uEB9F",
            _ => key switch { "active" => "\uE768", "unfinished" => "\uE896", "finished" => "\uE73E", _ => "\uE8B7" },
        };
    }

    public MainWindow(DownloadManager manager)
    {
        InitializeComponent();
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/app.ico", UriKind.Absolute));
        WindowPlace.Restore(this);
        this.manager = manager;
        Grid.ItemsSource = manager.Rows;
        BuildSidebar();
        ApplyThemeControlStyles();
        ThemeManager.ThemeChanged += ApplyThemeControlStyles;
        manager.Queues.CollectionChanged += (_, _) => BuildSidebar();
        InputBindings.Add(new KeyBinding(new Cmd(() => OnAdd(this, new RoutedEventArgs())), Key.N, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new Cmd(() => OnRemove(this, new RoutedEventArgs())), Key.Delete, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new Cmd(() => Search.Focus()), Key.F, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new Cmd(() => HelpWindows.ShowHelp(this)), Key.F1, ModifierKeys.None));
        void Shortcut(Key key, ModifierKeys mods, RoutedEventHandler handler) =>
            InputBindings.Add(new KeyBinding(new Cmd(() => { if (Keyboard.FocusedElement is not TextBox) handler(this, new RoutedEventArgs()); }), key, mods));
        Shortcut(Key.R, ModifierKeys.Control, OnResume);
        Shortcut(Key.OemPeriod, ModifierKeys.Control, OnPause);
        Shortcut(Key.R, ModifierKeys.Control | ModifierKeys.Shift, OnResumeAll);
        Shortcut(Key.P, ModifierKeys.Control | ModifierKeys.Shift, OnPauseAll);
        Shortcut(Key.O, ModifierKeys.Control, OnOpen);
        Shortcut(Key.I, ModifierKeys.Control, OnProperties);
        Shortcut(Key.T, ModifierKeys.Control | ModifierKeys.Shift, OnTorrents);
        Shortcut(Key.B, ModifierKeys.Control | ModifierKeys.Shift, OnBatch);
        Shortcut(Key.G, ModifierKeys.Control | ModifierKeys.Shift, OnGrabber);
        Shortcut(Key.L, ModifierKeys.Control | ModifierKeys.Shift, OnPlaylist);
        Shortcut(Key.L, ModifierKeys.Control, OnLibrary);
        VideoSubtitles.Enabled = manager.Settings.VideoSubtitles;
        toolbarElements = Toolbar.Children.Cast<UIElement>().ToArray();
        toolbarButtons = toolbarElements.OfType<Button>().ToArray();
        ToolbarHost.SizeChanged += (_, _) => UpdateToolbarOverflow();
        Loaded += (_, _) => UpdateToolbarOverflow();
        ApplyToolbar();
        UpdateButtons();
        UpdateStatus();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => { ShowDetails(); UpdateStatus(); };
        timer.Start();
    }

    // Closing the window keeps downloads running in the tray.
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        manager.Save();
        WindowPlace.Save(this);
        Hide();
    }

    // Explicit styles in the main window override theme dictionaries; switch them deliberately.
    void ApplyThemeControlStyles()
    {
        if (!IsInitialized) return;
        bool paper = ThemeManager.CurrentTheme == "PaperGray";
        var toolbarStyle = (Style)FindResource(paper ? "PaperGrayToolbarButtonStyle" : "ToolButton");
        foreach (var button in Toolbar.Children.OfType<Button>())
        {
            button.Style = toolbarStyle;
            button.MinWidth = ToolbarButtonMinWidth;
            button.MinHeight = ToolbarButtonMinHeight;
            button.Margin = ToolbarButtonMargin;
            button.Padding = ToolbarButtonPadding;
        }
        Dispatcher.BeginInvoke(new Action(UpdateToolbarOverflow), System.Windows.Threading.DispatcherPriority.Loaded);
        // Recreate rows to apply the PaperGray selection animations and padding-aware template.
        BuildSidebar();
    }

    // Keep the search box visible. Any buttons that do not fit become genuine menu commands.
    // A toolbar button's click handler remains the source of truth (including enabled state).
    void UpdateToolbarOverflow()
    {
        if (updatingToolbarOverflow || toolbarElements.Length == 0 || ToolbarHost.ActualWidth <= 0)
            return;

        updatingToolbarOverflow = true;
        try
        {
            if (!manager.Settings.ShowToolbar)
            {
                ToolbarOverflowButton.Visibility = Visibility.Collapsed;
                return;
            }

            // Restoring all items before measuring handles both window growth and shrinking.
            foreach (var element in toolbarElements)
            {
                element.Visibility = Visibility.Visible;
                element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            }

            double TotalWidth() => toolbarElements.Where(element => element.Visibility == Visibility.Visible)
                .Sum(element => element.DesiredSize.Width);
            double available = ToolbarHost.ActualWidth;

            if (TotalWidth() <= available)
            {
                ToolbarOverflowButton.Visibility = Visibility.Collapsed;
                ToolbarOverflowButton.ContextMenu = null;
                return;
            }

            ToolbarOverflowButton.Visibility = Visibility.Visible;
            ToolbarOverflowButton.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            available = Math.Max(0, available - ToolbarOverflowButton.DesiredSize.Width);

            // Keep the original ordering of actions. Hide the least prominent actions from
            // the right-hand end first, without splitting a button's icon and caption.
            foreach (var button in toolbarButtons.AsEnumerable().Reverse())
            {
                if (TotalWidth() <= available) break;
                button.Visibility = Visibility.Collapsed;
                UpdateToolbarSeparators();
            }
            UpdateToolbarSeparators();

            var overflow = new ContextMenu
            {
                PlacementTarget = ToolbarOverflowButton,
                Placement = PlacementMode.Bottom,
            };
            foreach (var button in toolbarButtons.Where(b => b.Visibility == Visibility.Collapsed))
            {
                var title = (button.Content as StackPanel)?.Children.OfType<TextBlock>().LastOrDefault()?.Text
                            ?? button.ToolTip?.ToString() ?? "Action";
                var item = new MenuItem { Header = title, IsEnabled = button.IsEnabled, ToolTip = button.ToolTip };
                item.Click += (_, _) =>
                {
                    if (button.IsEnabled)
                        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                };
                overflow.Items.Add(item);
            }
            // Button enabled states change while downloads run: read them on each opening.
            overflow.Opened += (_, _) =>
            {
                var hidden = toolbarButtons.Where(b => b.Visibility == Visibility.Collapsed).ToArray();
                for (int i = 0; i < hidden.Length && i < overflow.Items.Count; i++)
                    ((MenuItem)overflow.Items[i]).IsEnabled = hidden[i].IsEnabled;
            };
            ToolbarOverflowButton.ContextMenu = overflow;
        }
        finally { updatingToolbarOverflow = false; }
    }

    void UpdateToolbarSeparators()
    {
        for (int i = 0; i < toolbarElements.Length; i++)
        {
            if (toolbarElements[i] is not Border separator) continue;
            int previous = i - 1;
            while (previous >= 0 && toolbarElements[previous] is not Border) previous--;
            int next = i + 1;
            while (next < toolbarElements.Length && toolbarElements[next] is not Border) next++;
            bool left = toolbarElements.Skip(previous + 1).Take(i - previous - 1)
                .OfType<Button>().Any(b => b.Visibility == Visibility.Visible);
            bool right = toolbarElements.Skip(i + 1).Take(next - i - 1)
                .OfType<Button>().Any(b => b.Visibility == Visibility.Visible);
            separator.Visibility = left && right ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    void OnToolbarOverflow(object sender, RoutedEventArgs e)
    {
        if (ToolbarOverflowButton.ContextMenu is { } menu)
            menu.IsOpen = true;
    }

    // ---- sidebar ----

    public void BuildSidebar()
    {
        rebuilding = true;
        Sidebar.Items.Clear();
        var sidebarStyle = ThemeManager.CurrentTheme == "PaperGray"
            ? (Style)FindResource(typeof(ListBoxItem)) : (Style)FindResource("SidebarEntryStyle");
        void Header(string text) => Sidebar.Items.Add(new ListBoxItem
        {
            Content = text.ToUpperInvariant(), Style = sidebarStyle, IsEnabled = false, FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.Gray, Margin = new Thickness(4, 8, 0, 2),
        });
        void Entry(string text, string key, int indent = 0)
        {
            var row = new DockPanel { LastChildFill = true, MinWidth = 150 };
            var count = new TextBlock { Text = CountFor(key), Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(count, Dock.Right);
            row.Children.Add(count);
            row.Children.Add(new TextBlock { Text = IconFor(key), FontFamily = CategoryIconFont,
                Width = 22, FontSize = 15, VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.FindResource("AccentBrush") });
            row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            Sidebar.Items.Add(new ListBoxItem { Content = row, Tag = key, Style = sidebarStyle, Padding = new Thickness(14 + indent * 16, 6, 4, 6), MinHeight = 36, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        // Like IDM: each list opens into the file types it holds (sorted automatically by file type).
        static string Label(string c) => L.T(c == nameof(FileCategory.Other) ? "General" : c);
        void Types(string prefix) { foreach (var c in Enum.GetNames<FileCategory>()) Entry(Label(c), prefix + c, 1); }
        Header(L.T("Downloads"));
        Entry(L.T("All Downloads"), "all");
        Types("cat:");
        Entry(L.T("In Progress"), "active");
        Entry(L.T("Unfinished"), "unfinished");
        Types("ucat:");
        Entry(L.T("Finished"), "finished");
        Types("fcat:");
        Header(L.T("Queues"));
        foreach (var q in manager.Queues) Entry((q.Running ? "▶ " : "⏸ ") + (q.IsMain ? L.T(q.Name) : q.Name), "queue:" + q.Id);
        Sidebar.SelectedItem = Sidebar.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string?)i.Tag == filter)
                               ?? Sidebar.Items.OfType<ListBoxItem>().First(i => (string?)i.Tag == "all");
        rebuilding = false;
        ApplyFilter();
    }

    static bool Matches(string f, DownloadRecord r) => f switch
    {
        "active" => r.IsActive || r.Status == DownloadStatus.Queued,
        "unfinished" => r.Status != DownloadStatus.Finished,
        "finished" => r.Status == DownloadStatus.Finished,
        _ when f.StartsWith("cat:") => r.Category.ToString() == f[4..],
        _ when f.StartsWith("ucat:") => r.Status != DownloadStatus.Finished && r.Category.ToString() == f[5..],
        _ when f.StartsWith("fcat:") => r.Status == DownloadStatus.Finished && r.Category.ToString() == f[5..],
        _ when f.StartsWith("queue:") => r.Queue == f[6..],
        _ => true,
    };

    string CountFor(string key)
    {
        var n = manager.Rows.Count(r => Matches(key, r.Record));
        return n > 0 ? n.ToString() : "";
    }

    void UpdateStatus()
    {
        var active = manager.Rows.Count(r => r.Record.IsActive);
        var queued = manager.Rows.Count(r => r.Record.Status == DownloadStatus.Queued);
        var speed = manager.Rows.Where(r => r.Record.IsActive).Sum(r => r.SpeedBytes);
        var selected = Selected;
        StatusLeft.Text = $"{active} downloading   ·   {queued} queued" + (speed > 0 ? $"   ·   {Row.Bytes((long)speed)}/s" : "")
            + (selected.Count > 1 ? $"   ·   {selected.Count} selected, {Row.Bytes(selected.Sum(r => r.Record.Size ?? 0))}" : "");
        StatusRight.Text = (manager.Settings.SpeedLimitKBps > 0 ? $"Limit {manager.Settings.SpeedLimitKBps} KB/s   ·   " : "")
                           + $"{manager.Queues.Count(q => q.Running)} of {manager.Queues.Count} queues running";
        var view = CollectionViewSource.GetDefaultView(Grid.ItemsSource);
        var empty = view.IsEmpty;
        Empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Grid.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = Search.Text.Length > 0 ? $"Nothing matches “{Search.Text}”" : "No downloads here yet";
        if (++statusTicks % 4 == 0)
            foreach (var item in Sidebar.Items.OfType<ListBoxItem>())
                if (item is { Tag: string key, Content: DockPanel dp } && dp.Children[0] is TextBlock c) c.Text = CountFor(key);
    }

    int statusTicks;

    void OnSearch(object s, TextChangedEventArgs e)
    {
        SearchHint.Visibility = Search.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        ApplyFilter();
        UpdateStatus();
    }

    string CurrentQueue => filter.StartsWith("queue:") ? filter[6..] : DownloadQueue.MainId;

    void OnFilter(object s, SelectionChangedEventArgs e)
    {
        if (rebuilding || Sidebar.SelectedItem is not ListBoxItem { Tag: string key }) return;
        filter = key;
        ApplyFilter();
    }

    void ApplyFilter()
    {
        var f = filter;
        var q = Search.Text.Trim();
        CollectionViewSource.GetDefaultView(Grid.ItemsSource).Filter = o =>
        {
            var r = ((Row)o).Record;
            return Matches(f, r) && (q.Length == 0 || r.FileName.Contains(q, StringComparison.OrdinalIgnoreCase)
                                     || r.Url.Contains(q, StringComparison.OrdinalIgnoreCase));
        };
    }

    // ---- selection and details ----

    List<Row> Selected => Grid.SelectedItems.OfType<Row>().ToList();
    Row? One => Grid.SelectedItems.Count == 1 ? Grid.SelectedItem as Row : null;

    void OnSelection(object s, SelectionChangedEventArgs e) { ShowDetails(); UpdateButtons(); }

    /// <summary>Resume, Stop and Delete work on the selection, so they are greyed out without one (as in IDM).</summary>
    void UpdateButtons()
    {
        var any = Grid.SelectedItems.Count > 0;
        ResumeButton.IsEnabled = StopButton.IsEnabled = DeleteButton.IsEnabled = any;
    }

    /// <summary>View menu: show or hide the toolbar buttons and their names (saved with the settings).</summary>
    void ApplyToolbar()
    {
        var showToolbar = manager.Settings.ShowToolbar;
        var showNames = manager.Settings.ToolbarText;
        Toolbar.Visibility = showToolbar ? Visibility.Visible : Visibility.Collapsed;
        ShowToolbarItem.IsChecked = showToolbar;
        ShowNamesItem.IsChecked = showNames;
        ShowNamesItem.IsEnabled = showToolbar;
        UnzipItem.IsChecked = manager.Settings.ExtractZips;
        SubtitlesItem.IsChecked = manager.Settings.VideoSubtitles;
        foreach (var b in Toolbar.Children.OfType<Button>())
            if (b.Content is StackPanel p && p.Children.Count > 1) p.Children[1].Visibility = showNames ? Visibility.Visible : Visibility.Collapsed;
        Dispatcher.BeginInvoke(new Action(UpdateToolbarOverflow), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void OnToggleToolbar(object s, RoutedEventArgs e) { manager.Settings.ShowToolbar = ShowToolbarItem.IsChecked; manager.ApplySettings(manager.Settings); ApplyToolbar(); }
    void OnToggleUnzip(object s, RoutedEventArgs e) { manager.Settings.ExtractZips = UnzipItem.IsChecked; manager.ApplySettings(manager.Settings); }
    void OnToggleSubtitles(object s, RoutedEventArgs e)
    {
        manager.Settings.VideoSubtitles = VideoSubtitles.Enabled = SubtitlesItem.IsChecked;
        manager.ApplySettings(manager.Settings);
    }
    void OnPlaylist(object s, RoutedEventArgs e) => ShowPlaylist("");
    void OnLibrary(object s, RoutedEventArgs e) => Dialogs.MediaLibraryWindow(this, manager);

    /// <summary>Playlists and channels: pick their videos first, then they open in the Add window.</summary>
    void ShowPlaylist(string link)
    {
        var queue = CurrentQueue;
        Bring();
        Dialogs.Playlist(this, manager, link, links => Dispatcher.BeginInvoke(() => ShowAdd(links, queue)));
    }

    void OnToggleNames(object s, RoutedEventArgs e) { manager.Settings.ToolbarText = ShowNamesItem.IsChecked; manager.ApplySettings(manager.Settings); ApplyToolbar(); }

    void ShowDetails()
    {
        if (One is not { } row) { Details.Visibility = Visibility.Collapsed; return; }
        var r = row.Record;
        Details.Visibility = Visibility.Visible;
        DetailName.Text = r.FileName;
        DetailUrl.Text = r.Url;
        Bar.Update(r);
        var parts = new List<string> { $"{Row.Bytes(r.Received)} of {(r.Size is long s ? Row.Bytes(s) : "unknown size")}" };
        if (r.IsActive)
        {
            if (row.Speed.Length > 0) parts.Add(row.Speed);
            if (row.TimeLeft.Length > 0) parts.Add(row.TimeLeft + " left");
            if (r.SiteVideo == true) parts.Add("Video site · " + SiteVideo.ChoiceText(r.AudioOnly, r.MaxHeight));
            else if (r.IsStream == true) parts.Add($"Video pieces: {r.StreamDone ?? 0} of {r.StreamTotal ?? 0}");
            else lock (r.Segments) parts.Add($"Connections: {r.Segments.Count(x => !x.IsComplete)}");
        }
        parts.Add(r.AcceptsRanges || r.Status == DownloadStatus.Queued ? "Resume: yes" : "Resume: no");
        parts.Add("Queue: " + row.Queue);
        if (r.SpeedLimitKBps is int l) parts.Add($"Limit: {l} KB/s");
        if (r.ChecksumResult is { } c) parts.Add(c);
        DetailInfo.Text = string.Join("   ·   ", parts);
        DetailError.Text = r.Error ?? "";
    }

    // ---- toolbar ----

    void OnTorrents(object s, RoutedEventArgs e) => Dialogs.TorrentManager(this, manager, CurrentQueue);

    void OnAdd(object s, RoutedEventArgs e) => ShowAdd(Dialogs.ClipboardLink(), CurrentQueue);

    bool adding;

    public void ShowAdd(string links, string? queueId = null)
    {
        if (adding) return; // one Add window at a time (the clipboard may offer another link meanwhile)
        adding = true;
        try
        {
            Bring();
            if (links.Trim() is var one && !one.Contains('\n') && Playlists.IsPlaylist(one)) { adding = false; ShowPlaylist(one); return; }
            if (SmartDownload.Detect(links) == DownloadEngine.Torrent) Dialogs.Torrent(this, manager, links, queueId ?? DownloadQueue.MainId);
            else Dialogs.Add(this, manager, links, queueId ?? DownloadQueue.MainId);
        }
        finally { adding = false; }
    }

    public void Bring()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    void OnResume(object s, RoutedEventArgs e) { foreach (var r in Selected) manager.Resume(r); }
    void OnPause(object s, RoutedEventArgs e) { foreach (var r in Selected) manager.Pause(r); }
    void OnRemove(object s, RoutedEventArgs e) => ConfirmRemove(Selected, toRecycleBin: false);

    void OnSelectAll(object s, RoutedEventArgs e)
    {
        if (Grid.Items.Count > 0 && Grid.SelectedItems.Count == Grid.Items.Count) Grid.UnselectAll();
        else Grid.SelectAll();
        Grid.Focus();
    }

    /// <summary>Asks before anything is removed. Files stay unless the user chose the Recycle Bin.</summary>
    void ConfirmRemove(List<Row> rows, bool toRecycleBin)
    {
        if (rows.Count == 0) return;
        var what = rows.Count == 1 ? "this download" : $"these {rows.Count} downloads";
        var text = toRecycleBin
            ? $"Delete {what} and move the {(rows.Count == 1 ? "file" : "files")} to the Recycle Bin?\n\nYou can still restore them from the Recycle Bin."
            : $"Remove {what} from the list?\n\nThe downloaded files stay on your PC.";
        if (MessageBox.Show(this, text, "Delete", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        foreach (var r in rows)
        {
            if (toRecycleBin && r.Record.SavedPath is { } p && System.IO.File.Exists(p))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(p, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            manager.Remove(r);
        }
    }
    void OnPauseAll(object s, RoutedEventArgs e) => manager.PauseAll();
    void OnResumeAll(object s, RoutedEventArgs e) => manager.ResumeAll();
    void OnExit(object s, RoutedEventArgs e) { manager.Save(); WindowPlace.Save(this); manager.RequestQuit(); }
    void OnClear(object s, RoutedEventArgs e)
    {
        var done = manager.Rows.Where(r => r.Record.Status == DownloadStatus.Finished).ToList();  // only the ones counted in the question
        var n = done.Count;
        if (n == 0) return;
        if (MessageBox.Show(this, $"Remove {n} completed {(n == 1 ? "download" : "downloads")} from the list?\n\nThe downloaded files stay on your PC.",
                "Delete Completed", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK)
            foreach (var r in done) manager.Remove(r);
    }
    void OnScheduler(object s, RoutedEventArgs e) { Dialogs.Scheduler(this, manager, CurrentQueue); BuildSidebar(); }
    void OnStartQueue(object s, RoutedEventArgs e) { manager.StartQueue(CurrentQueue); BuildSidebar(); }
    void OnStopQueue(object s, RoutedEventArgs e) { manager.StopQueue(CurrentQueue); BuildSidebar(); }
    void OnGrabber(object s, RoutedEventArgs e) => Dialogs.Grabber(this, manager, CurrentQueue);
    void OnBatch(object s, RoutedEventArgs e) => Dialogs.Batch(this, manager, CurrentQueue);
    void OnSettings(object s, RoutedEventArgs e) => Dialogs.Options(this, manager);
    void OnHelp(object s, RoutedEventArgs e) => HelpWindows.ShowHelp(this);
    void OnAbout(object s, RoutedEventArgs e) => HelpWindows.ShowAbout(this);

    void OnRemoveFile(object s, RoutedEventArgs e) => ConfirmRemove(Selected, toRecycleBin: true);

    void OnOpen(object s, RoutedEventArgs e)
    {
        if (One?.Record.SavedPath is { } p) Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
    }

    /// <summary>Double-click: a finished download opens its file; any other shows its progress window.</summary>
    void OnRowDoubleClick(object s, RoutedEventArgs e)
    {
        if (One is not { } row) return;
        if (row.Record.Status == DownloadStatus.Finished && row.Record.SavedPath is { } p) Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
        else DownloadWindows.ShowProgress(manager, row);
    }

    void OnShowInFolder(object s, RoutedEventArgs e)
    {
        if (One?.Record.SavedPath is { } p) Process.Start("explorer.exe", $"/select,\"{p}\"");
    }

    void OnShowProgress(object s, RoutedEventArgs e) { if (One is { } r) DownloadWindows.ShowProgress(manager, r); }
    void OnShowQr(object s, RoutedEventArgs e) => Dialogs.ShowQr(this, Selected.Select(r => r.Record.Url).ToList());
    void OnCopyLink(object s, RoutedEventArgs e) { if (One is { } r) Clipboard.SetText(r.Record.Url); }
    void OnRefreshLink(object s, RoutedEventArgs e) { if (One is { } r) Dialogs.RefreshLink(this, manager, r); }
    void OnProperties(object s, RoutedEventArgs e) { if (One is { } r) Dialogs.Properties(this, manager, r); }

    protected override void OnContextMenuOpening(ContextMenuEventArgs e)
    {
        MoveMenu.Items.Clear();
        foreach (var q in manager.Queues)
        {
            var item = new MenuItem { Header = q.Name };
            var id = q.Id;
            item.Click += (_, _) =>
            {
                foreach (var r in Selected)
                    manager.SetDetails(r, r.Record.Username ?? "", r.Record.Password ?? "", r.Record.Checksum ?? "", r.Record.SpeedLimitKBps ?? 0, id);
            };
            MoveMenu.Items.Add(item);
        }
        base.OnContextMenuOpening(e);
    }

    /// <summary>A dropped .torrent file opens Add Torrent; dropped links (or text) open Add Download.</summary>
    void OnDrop(object s, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            var torrent = files.FirstOrDefault(p => p.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(p));
            if (torrent != null) Dialogs.Torrent(this, manager, torrent, CurrentQueue);
            return;
        }
        if (e.Data.GetData(DataFormats.Text) is string text) ShowAdd(text, CurrentQueue);
    }

    void OnDragOver(object s, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    sealed class Cmd(Action run) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? p) => true;
        public void Execute(object? p) => run();
    }
}

/// <summary>One bar for the whole file; each range shows how much of it has arrived.</summary>
public sealed class SegmentBar : FrameworkElement
{
    List<(long Start, long Written, bool Done)> parts = new();
    long size;
    static readonly Brush Back = new SolidColorBrush(Color.FromRgb(0xE3, 0xE8, 0xEF));
    static readonly Brush Busy = new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xEB));
    static readonly Brush Done = new SolidColorBrush(Color.FromRgb(0x2D, 0xA4, 0x4E));

    public void Update(DownloadRecord r)
    {
        lock (r.Segments) parts = r.Segments.Select(s => (s.Start, s.Written, s.IsComplete)).ToList();
        size = r.Size ?? 0;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(Back, null, new Rect(0, 0, ActualWidth, ActualHeight), 3, 3);
        if (size <= 0) return;
        foreach (var (start, written, done) in parts)
        {
            var x = ActualWidth * start / size;
            var w = Math.Max(0.5, ActualWidth * written / size);
            dc.DrawRectangle(done ? Done : Busy, null, new Rect(x, 0, w, ActualHeight));
        }
    }
}
