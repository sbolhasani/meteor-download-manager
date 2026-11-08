using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Nexonic.Core;

namespace Nexonic.App;

/// <summary>The app's small windows, built in code so each one reads top to bottom.</summary>
public static partial class Dialogs
{
    static readonly Brush Muted = Brushes.DimGray;

    public static string ClipboardLink()
    {
        try
        {
            var t = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
            return new DownloadJob(t).IsValid ? t : "";
        }
        catch { return ""; }
    }

    // ---- building blocks ----

    sealed class Form
    {
        public readonly Window Window;
        public readonly StackPanel Body = new() { Margin = new Thickness(16) };
        readonly WrapPanel buttons = new() { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        /// <summary>Where Note, Field and Check add their controls: the window's body, or a section made by <see cref="Collapsed"/>.</summary>
        public Panel Target;

        public Form(Window owner, string title, double width = 520)
        {
            Target = Body;
            Body.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
            var outer = new StackPanel();
            outer.Children.Add(Body);
            Window = new Window
            {
                Title = title, Owner = owner, Width = width, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                Content = new ScrollViewer { Content = outer, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 760 },
            };
            Body.Loaded += (_, _) => { if (!Body.Children.Contains(buttons)) Body.Children.Add(buttons); };
        }

        public TextBlock Note(string text)
        {
            var t = new TextBlock { Text = text, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            Target.Children.Add(t);
            return t;
        }

        public T Field<T>(string label, T control) where T : FrameworkElement
        {
            Target.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2) });
            control.Margin = new Thickness(0, 0, 0, 4);
            Target.Children.Add(control);
            return control;
        }

        public TextBox Text(string label, string value = "", bool multi = false)
        {
            var box = new TextBox { Text = value, Padding = new Thickness(4, 3, 4, 3) };
            if (multi)
            {
                box.AcceptsReturn = true;
                box.Height = 110;
                box.TextWrapping = TextWrapping.NoWrap;
                box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                box.FontFamily = new FontFamily("Consolas");
            }
            return Field(label, box);
        }

        public CheckBox Check(string label, bool value)
        {
            var c = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 6, 0, 2) };
            Target.Children.Add(c);
            return c;
        }

        /// <summary>A section that starts closed; the controls <paramref name="build"/> adds go inside it.</summary>
        public Expander Collapsed(string header, Action build)
        {
            var inner = new StackPanel { Margin = new Thickness(18, 4, 0, 0) };
            var section = new Expander { Header = header, Content = inner, IsExpanded = false, Margin = new Thickness(0, 8, 0, 4) };
            Target.Children.Add(section);
            var outer = Target; Target = inner;
            try { build(); } finally { Target = outer; }
            return section;
        }

        public ComboBox Queue(DownloadManager m, string selected)
        {
            var box = new ComboBox { DisplayMemberPath = "Name", ItemsSource = m.Queues.ToList() };
            box.SelectedItem = m.Queues.FirstOrDefault(q => q.Id == selected) ?? m.Queues.First();
            return Field("Queue", box);
        }

        public TextBox Folder(string value)
        {
            var box = new TextBox { Text = value, Padding = new Thickness(4, 3, 4, 3) };
            var pick = new Button { Content = "Choose…", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };
            pick.Click += (_, _) =>
            {
                var d = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(box.Text) ? box.Text : "" };
                if (d.ShowDialog(Window) == true) box.Text = d.FolderName;
            };
            var row = new DockPanel();
            DockPanel.SetDock(pick, Dock.Right);
            row.Children.Add(pick);
            row.Children.Add(box);
            Field("Save to", row);
            return box;
        }

        public Button AddButton(string text, Action click, bool isDefault = false, bool isCancel = false)
        {
            var b = new Button { Content = text, MinWidth = 90, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0), IsDefault = isDefault, IsCancel = isCancel };
            b.Click += (_, _) => click();
            buttons.Children.Add(b);
            return b;
        }

        public void Show() => Window.ShowDialog();
        public void Close() => Window.Close();
    }

    static int Number(string text, int fallback = 0) => int.TryParse(text.Trim(), out var n) && n >= 0 ? n : fallback;

    // ---- Add ----

    public static void Add(Window owner, DownloadManager m, string links, string queueId)
    {
        var f = new Form(owner, "Add Download", 560);
        f.Note("Paste one or more links, one per line.");
        var text = f.Text("Links", links, multi: true);
        var name = f.Text("File name (one link only; empty = from the link)");
        var folder = f.Folder(m.Settings.Folder);
        var queue = f.Queue(m, queueId);
        // One video link: looked up while the window is open, with its qualities and sizes. Several links: sound only or not.
        var qualityPanel = new StackPanel { Visibility = Visibility.Collapsed };
        qualityPanel.Children.Add(new TextBlock { Text = "Quality", Margin = new Thickness(0, 6, 0, 2) });
        var quality = new ComboBox { Margin = new Thickness(0, 0, 0, 4) };
        qualityPanel.Children.Add(quality);
        var qualityNote = new TextBlock { Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        qualityPanel.Children.Add(qualityNote);
        f.Body.Children.Add(qualityPanel);
        var audio = f.Check("Sound only (for YouTube and other video sites)", false);
        f.Note("Video sites are saved in the best quality available, with picture and sound joined.");
        var tags = new List<int> { 0 };
        int Chosen() => quality.SelectedIndex >= 0 && quality.SelectedIndex < tags.Count ? tags[quality.SelectedIndex] : 0;
        string? OneVideo()
        {
            var ls = Links.Split(text.Text);
            return ls.Count == 1 && SiteVideo.IsSupported(ls[0]) ? ls[0] : null;
        }
        VideoPrefetch? prefetch = null;
        void Refresh()
        {
            var one = OneVideo() != null;
            qualityPanel.Visibility = one ? Visibility.Visible : Visibility.Collapsed;
            audio.Visibility = one ? Visibility.Collapsed : Visibility.Visible;
            if (!one || prefetch == null) return;
            var keep = Chosen();
            var options = VideoPrefetch.Options(prefetch.Choices);
            tags = options.Select(o => o.Tag).ToList();
            quality.ItemsSource = options.Select(o => o.Label).ToList();
            quality.SelectedIndex = Math.Max(0, tags.IndexOf(keep));
            var size = VideoPrefetch.SizeFor(prefetch.Info, prefetch.Choices, Chosen());
            qualityNote.Text = prefetch.Busy ? "Finding the qualities on offer…"
                : prefetch.Info is { } info ? info.Title + (size is long b ? " · " + Row.Bytes(b) : "")
                : "The qualities are found when the download starts.";
        }
        var pf = prefetch = new VideoPrefetch(() => m.Settings.Proxy, () => f.Window.Dispatcher.BeginInvoke(new Action(Refresh)));
        quality.SelectionChanged += (_, _) =>
        {
            var size = pf.Info == null ? null : VideoPrefetch.SizeFor(pf.Info, pf.Choices, Chosen());
            if (pf.Info is { } info) qualityNote.Text = info.Title + (size is long b ? " · " + Row.Bytes(b) : "");
        };
        text.TextChanged += (_, _) => { pf.Want(OneVideo()); Refresh(); };
        f.Window.Closed += (_, _) => pf.Dispose();
        pf.Want(OneVideo());
        Refresh();
        var more = new Expander { Header = "Login, checksum and speed limit", Margin = new Thickness(0, 8, 0, 0) };
        var inner = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        TextBox Inner(string label)
        {
            inner.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2) });
            var t = new TextBox { Padding = new Thickness(4, 3, 4, 3) };
            inner.Children.Add(t);
            return t;
        }
        var user = Inner("User name");
        inner.Children.Add(new TextBlock { Text = "Password", Margin = new Thickness(0, 6, 0, 2) });
        var pass = new PasswordBox { Padding = new Thickness(4, 3, 4, 3) };
        inner.Children.Add(pass);
        var sum = Inner("Checksum (MD5, SHA-1, SHA-256 or SHA-512)");
        var limit = Inner("Speed limit in KB/s (0 = none)");
        more.Content = inner;
        f.Body.Children.Add(more);
        var error = f.Note("");
        error.Foreground = Brushes.Firebrick;
        void Go(bool start)
        {
            if (sum.Text.Trim().Length > 0 && Checksum.Parse(sum.Text) == null) { error.Text = "The checksum is not a valid value."; return; }
            var n = m.AddLinks(text.Text, new AddOptions
            {
                Folder = folder.Text.Trim().Length > 0 ? folder.Text.Trim() : m.Settings.Folder,
                FileName = name.Text.Trim(),
                QueueId = (queue.SelectedItem as DownloadQueue)?.Id ?? DownloadQueue.MainId,
                Start = start,
                Username = user.Text.Trim(),
                Password = pass.Password,
                Checksum = sum.Text.Trim(),
                SpeedLimitKBps = Number(limit.Text),
                AudioOnly = OneVideo() != null ? Chosen() == -1 : audio.IsChecked == true,
                MaxHeight = OneVideo() != null && Chosen() > 0 ? Chosen() : null,
                // Reuse the details looked up while the window was open (not when a login is needed to see the video).
                InfoJson = OneVideo() is { } one && user.Text.Trim().Length == 0 ? prefetch!.Take(one) : null,
            });
            if (n == 0) { error.Text = "Paste at least one http or https link."; return; }
            f.Close();
        }
        f.AddButton("Cancel", f.Close, isCancel: true);
        f.AddButton("Download Later", () => Go(false));
        f.AddButton("Start Download", () => Go(true), isDefault: true);
        f.Show();
    }

    // ---- Batch ----

    public static void Batch(Window owner, DownloadManager m, string queueId)
    {
        var f = new Form(owner, "Batch Download", 600);
        f.Note("Put a range in square brackets: [001-100] keeps three digits, [1-100] does not, [a-z] uses letters and [0-100:5] counts in fives. Several ranges multiply.");
        var pattern = f.Text("Pattern", "https://example.com/photos/img[001-050].jpg");
        pattern.FontFamily = new FontFamily("Consolas");
        var preview = f.Note("");
        void Update()
        {
            var n = BatchPattern.Count(pattern.Text);
            if (n > BatchPattern.Limit) { preview.Text = $"{n} links – the limit is {BatchPattern.Limit}"; return; }
            var all = BatchPattern.Expand(pattern.Text);
            preview.Text = n > 1 ? $"{n} links\nFirst: {all[0]}\nLast: {all[^1]}" : $"{n} link";
        }
        pattern.TextChanged += (_, _) => Update();
        Update();
        var folder = f.Folder(m.Settings.Folder);
        var queue = f.Queue(m, queueId);
        void Go(bool start)
        {
            if (BatchPattern.Count(pattern.Text) > BatchPattern.Limit) return;
            m.AddLinks(string.Join("\n", BatchPattern.Expand(pattern.Text)), new AddOptions
            {
                Folder = folder.Text.Trim(), QueueId = (queue.SelectedItem as DownloadQueue)?.Id ?? DownloadQueue.MainId, Start = start,
            });
            f.Close();
        }
        f.AddButton("Cancel", f.Close, isCancel: true);
        f.AddButton("Download Later", () => Go(false));
        f.AddButton("Start Download", () => Go(true), isDefault: true);
        f.Show();
    }

    // ---- Scheduler ----

    static readonly string[] DayNames = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

    public static void Scheduler(Window owner, DownloadManager m, string selectedId)
    {
        var f = new Form(owner, "Scheduler", 700);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var list = new ListBox { Height = 320, DisplayMemberPath = "Name" };
        var addBtn = new Button { Content = "New Queue", Margin = new Thickness(0, 6, 0, 0) };
        var delBtn = new Button { Content = "Delete Queue", Margin = new Thickness(0, 4, 0, 0) };
        left.Children.Add(list);
        left.Children.Add(addBtn);
        left.Children.Add(delBtn);
        var editor = new StackPanel();
        Grid.SetColumn(editor, 1);
        grid.Children.Add(left);
        grid.Children.Add(editor);
        f.Body.Children.Add(grid);

        void Refill(DownloadQueue? select)
        {
            list.ItemsSource = null;
            list.ItemsSource = m.Queues.ToList();
            list.SelectedItem = select ?? m.Queues.First();
        }

        void Edit(DownloadQueue q)
        {
            editor.Children.Clear();
            TextBlock Label(string t) { var l = new TextBlock { Text = t, Margin = new Thickness(0, 8, 0, 2) }; editor.Children.Add(l); return l; }
            Label("Name");
            var name = new TextBox { Text = q.Name, IsEnabled = !q.IsMain, Padding = new Thickness(4, 3, 4, 3) };
            name.LostFocus += (_, _) => { if (name.Text.Trim().Length > 0) { q.Name = name.Text.Trim(); m.SaveQueues(); } };
            editor.Children.Add(name);

            var start = new CheckBox { Content = "Start at (HH:mm)", IsChecked = q.StartEnabled, Margin = new Thickness(0, 10, 0, 2) };
            var startTime = new TextBox { Text = q.StartTime, Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
            var days = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            for (var d = 1; d <= 7; d++)
            {
                var day = d;
                var c = new CheckBox { Content = DayNames[d - 1], IsChecked = q.Days.Contains(d), Margin = new Thickness(0, 0, 10, 0) };
                c.Click += (_, _) =>
                {
                    var set = q.Days.ToHashSet();
                    if (c.IsChecked == true) set.Add(day); else set.Remove(day);
                    q.Days = set.OrderBy(x => x).ToList();
                    m.SaveQueues();
                };
                days.Children.Add(c);
            }
            var stop = new CheckBox { Content = "Stop at (HH:mm)", IsChecked = q.StopEnabled, Margin = new Thickness(0, 10, 0, 2) };
            var stopTime = new TextBox { Text = q.StopTime, Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var e in new UIElement[] { start, startTime, days, stop, stopTime }) editor.Children.Add(e);

            Label("Downloads at the same time");
            var active = new TextBox { Text = (q.IsMain ? m.Settings.MaxActive : q.MaxActive).ToString(), Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
            editor.Children.Add(active);
            Label("When the queue is done");
            var after = new ComboBox { ItemsSource = Enum.GetValues<AfterQueue>().Select(DownloadQueue.AfterTitle).ToList(), SelectedIndex = (int)q.AfterDone };
            editor.Children.Add(after);
            var waiting = m.Rows.Count(r => r.Record.Queue == q.Id && r.Record.Status != DownloadStatus.Finished);
            var info = new TextBlock { Text = $"{waiting} file(s) waiting · {(q.Running ? "Running" : "Stopped")}", Foreground = Muted, Margin = new Thickness(0, 10, 0, 0) };
            editor.Children.Add(info);
            var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var apply = new Button { Content = "Apply", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 6, 0) };
            var go = new Button { Content = "Start Now", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 6, 0) };
            var halt = new Button { Content = "Stop", Padding = new Thickness(12, 3, 12, 3) };
            row.Children.Add(apply);
            row.Children.Add(go);
            row.Children.Add(halt);
            editor.Children.Add(row);
            var error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
            editor.Children.Add(error);

            static bool IsTime(string t) => TimeSpan.TryParseExact(t.Trim(), @"hh\:mm", null, out var v) && v < TimeSpan.FromDays(1);
            void Apply()
            {
                if (!IsTime(startTime.Text) || !IsTime(stopTime.Text)) { error.Text = "Write times as HH:mm, for example 02:30."; return; }
                error.Text = "";
                if (name.Text.Trim().Length > 0 && !q.IsMain) q.Name = name.Text.Trim();
                q.StartEnabled = start.IsChecked == true;
                q.StartTime = startTime.Text.Trim();
                q.StopEnabled = stop.IsChecked == true;
                q.StopTime = stopTime.Text.Trim();
                q.AfterDone = (AfterQueue)Math.Max(0, after.SelectedIndex);
                var n = Math.Clamp(Number(active.Text, 3), 1, 10);
                if (q.IsMain)
                {
                    var s = m.Settings;
                    s.MaxActive = n;
                    m.ApplySettings(s);
                }
                else q.MaxActive = n;
                m.SaveQueues();
                Refill(q);
            }
            apply.Click += (_, _) => Apply();
            go.Click += (_, _) => { Apply(); m.StartQueue(q.Id); Refill(q); };
            halt.Click += (_, _) => { m.StopQueue(q.Id); Refill(q); };
        }

        list.SelectionChanged += (_, _) => { if (list.SelectedItem is DownloadQueue q) Edit(q); };
        addBtn.Click += (_, _) => Refill(m.NewQueue());
        delBtn.Click += (_, _) => { if (list.SelectedItem is DownloadQueue { IsMain: false } q) { m.DeleteQueue(q); Refill(null); } };
        Refill(m.Queues.FirstOrDefault(q => q.Id == selectedId));
        f.AddButton("Done", f.Close, isDefault: true, isCancel: true);
        f.Show();
    }

    // ---- Grabber ----

    public static void Grabber(Window owner, DownloadManager m, string queueId)
    {
        var f = new Form(owner, "Site Grabber", 640);
        f.Note("Explores a website section and lists the files it links to. Only use it on sites you are allowed to download from.");
        var start = f.Text("Start page", ClipboardLink());
        var depth = f.Text("Follow links this many levels deep (0-5)", "1");
        var types = f.Text("File types (empty = every file)", "zip rar 7z pdf mp3 mp4 jpg png");
        var stay = f.Check("Stay inside the start page's folder", true);
        var bar = new DockPanel { Margin = new Thickness(0, 8, 0, 4) };
        var explore = new Button { Content = "Explore", Padding = new Thickness(12, 3, 12, 3) };
        var status = new TextBlock { Foreground = Muted, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var all = new Button { Content = "All", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(4, 0, 0, 0) };
        var none = new Button { Content = "None", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(4, 0, 0, 0) };
        DockPanel.SetDock(explore, Dock.Left);
        DockPanel.SetDock(none, Dock.Right);
        DockPanel.SetDock(all, Dock.Right);
        bar.Children.Add(explore);
        bar.Children.Add(none);
        bar.Children.Add(all);
        bar.Children.Add(status);
        f.Body.Children.Add(bar);
        var results = new ListBox { Height = 180 };
        f.Body.Children.Add(results);
        var folder = f.Folder(m.Settings.Folder);
        var queue = f.Queue(m, queueId);
        CancellationTokenSource? cts = null;

        List<CheckBox> Boxes() => results.Items.OfType<CheckBox>().ToList();
        all.Click += (_, _) => Boxes().ForEach(b => b.IsChecked = true);
        none.Click += (_, _) => Boxes().ForEach(b => b.IsChecked = false);
        explore.Click += async (_, _) =>
        {
            if (cts != null) { cts.Cancel(); return; }
            if (!new DownloadJob(start.Text.Trim()).IsValid) { status.Text = "Enter an http or https address."; return; }
            var o = new GrabOptions
            {
                Start = new Uri(start.Text.Trim()), Depth = Math.Clamp(Number(depth.Text, 1), 0, 5),
                Types = FileTypes.Parse(types.Text), StayInFolder = stay.IsChecked == true,
            };
            cts = new CancellationTokenSource();
            explore.Content = "Stop";
            results.Items.Clear();
            status.Text = "Exploring…";
            var handler = Network.Handler(m.Settings.Proxy);
            handler.AllowAutoRedirect = true;
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            var progress = new Progress<(int Pages, int Files)>(p => status.Text = $"{p.Pages} pages read, {p.Files} files found");
            IProgress<(int, int)> report = progress;
            var files = await SiteGrabber.ExploreAsync(o, http, (p, n) => report.Report((p, n)), cts.Token);
            foreach (var u in files) results.Items.Add(new CheckBox { Content = u.AbsoluteUri, IsChecked = true });
            status.Text = $"{files.Count} files found";
            explore.Content = "Explore";
            cts = null;
        };
        void Go(bool startNow)
        {
            var links = Boxes().Where(b => b.IsChecked == true).Select(b => (string)b.Content).ToList();
            if (links.Count == 0) return;
            m.AddLinks(string.Join("\n", links), new AddOptions
            {
                Folder = folder.Text.Trim(), QueueId = (queue.SelectedItem as DownloadQueue)?.Id ?? DownloadQueue.MainId, Start = startNow,
            });
            cts?.Cancel();
            f.Close();
        }
        f.AddButton("Close", () => { cts?.Cancel(); f.Close(); }, isCancel: true);
        f.AddButton("Download Later", () => Go(false));
        f.AddButton("Download Checked", () => Go(true), isDefault: true);
        f.Show();
    }

    // ---- Properties ----

    public static void Properties(Window owner, DownloadManager m, Row row)
    {
        var r = row.Record;
        var f = new Form(owner, "Properties", 560);
        f.Note($"{r.FileName}\n{r.Url}\nFolder: {r.Folder}\nSize: {(r.Size is long s ? Row.Bytes(s) : "Unknown")}   Status: {row.Status}");
        var queue = f.Queue(m, r.Queue);
        var user = f.Text("User name", r.Username ?? "");
        var pass = f.Field("Password", new PasswordBox { Password = r.Password ?? "", Padding = new Thickness(4, 3, 4, 3) });
        var sum = f.Text("Expected checksum (MD5, SHA-1, SHA-256 or SHA-512)", r.Checksum ?? "");
        var result = f.Note(r.ChecksumResult ?? "");
        if (r.Status == DownloadStatus.Finished)
        {
            var check = new Button { Content = "Check Now", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 3, 12, 3) };
            check.Click += (_, _) => result.Text = m.Verify(row, sum.Text);
            f.Body.Children.Add(check);
        }
        var limit = f.Text("Speed limit in KB/s (0 = none)", (r.SpeedLimitKBps ?? 0).ToString());
        f.AddButton("Cancel", f.Close, isCancel: true);
        f.AddButton("Save", () =>
        {
            m.SetDetails(row, user.Text.Trim(), pass.Password, Checksum.Parse(sum.Text) == null ? "" : sum.Text.Trim(), Number(limit.Text),
                (queue.SelectedItem as DownloadQueue)?.Id ?? DownloadQueue.MainId);
            f.Close();
        }, isDefault: true);
        f.Show();
    }

    // ---- Refresh link ----

    public static void RefreshLink(Window owner, DownloadManager m, Row row)
    {
        var f = new Form(owner, "Refresh Link", 540);
        f.Note("Links from many sites stop working after a while. Paste a fresh link for the same file and the download continues from where it stopped. " +
               "Or choose Wait for Browser, then start the same download again in your browser: the extension hands the new link over automatically.");
        var link = f.Text("New link", ClipboardLink());
        f.AddButton("Cancel", f.Close, isCancel: true);
        f.AddButton("Wait for Browser", () => { m.Refresh(row, null); f.Close(); });
        f.AddButton("Use This Link", () => { if (new DownloadJob(link.Text.Trim()).IsValid) { m.Refresh(row, link.Text.Trim()); f.Close(); } }, isDefault: true);
        f.Show();
    }

    // ---- Options ----

    public static void Options(Window owner, DownloadManager m)
    {
        var s = m.Settings;
        var f = new Form(owner, "Options", 600);
        var tabs = new TabControl { Height = 430 };
        f.Body.Children.Add(tabs);
        StackPanel Tab(string header)
        {
            var p = new StackPanel { Margin = new Thickness(10) };
            tabs.Items.Add(new TabItem { Header = header, Content = new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
            return p;
        }
        TextBox Box(StackPanel p, string label, string value)
        {
            p.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2) });
            var t = new TextBox { Text = value, Padding = new Thickness(4, 3, 4, 3) };
            p.Children.Add(t);
            return t;
        }
        CheckBox Tick(StackPanel p, string label, bool value)
        {
            var c = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 8, 0, 0) };
            p.Children.Add(c);
            return c;
        }
        TextBlock Hint(StackPanel p, string text)
        {
            var t = new TextBlock { Text = text, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), FontSize = 11 };
            p.Children.Add(t);
            return t;
        }

        var general = Tab("General");
        general.Children.Add(new TextBlock { Text = "Appearance theme", Margin = new Thickness(0, 8, 0, 2) });
        var themeNames = ThemeManager.Names;
        var themePicker = new ComboBox { ItemsSource = themeNames, SelectedIndex = Math.Max(0, Array.IndexOf(themeNames, s.Theme)) };
        general.Children.Add(themePicker);
        var conn = Box(general, "Connections per download (1-32)", s.Connections.ToString());
        var active = Box(general, "Downloads at the same time (main queue)", s.MaxActive.ToString());
        var folder = Box(general, "Save to folder", s.Folder);
        var sort = Tick(general, "Put each type in its own folder (Video, Music, Documents, Programs, Compressed, Images)", s.SortIntoCategoryFolders);
        var notify = Tick(general, "Notifications", s.Notifications);
        var updates = Tick(general, "Check for updates when the app opens", s.CheckForUpdates);
        general.Children.Add(new TextBlock { Text = "Language", Margin = new Thickness(0, 10, 0, 2) });
        var languageChoices = new List<string> { "" }.Concat(L.Languages.Select(l => l.Code)).ToList();
        var language = new ComboBox
        {
            ItemsSource = new List<string> { L.T("Same as the system") }.Concat(L.Languages.Select(l => l.Reviewed ? l.Name : l.Name + " (machine translation)")).ToList(),
            SelectedIndex = Math.Max(0, languageChoices.IndexOf(s.Language)),
        };
        general.Children.Add(language);
        Hint(general, "The new language is used after the app restarts. Translations other than English are machine translations that still need review.");
        general.Children.Add(new TextBlock { Text = "Browser downloads", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0) });
        var askDialog = Tick(general, "Show the download window when a link is clicked in the browser", s.ShowDownloadDialog);
        var progressWin = Tick(general, "Show a progress window for each download I start", s.ShowProgressWindow);
        var toFront = Tick(general, "Bring the main window to the front for browser downloads", s.BringToFrontOnBrowserDownload);
        Hint(general, "With the download window off, downloads start right away. Closing the main window keeps the app running in the tray.");

        var connection = Tab("Connection");
        var limit = Box(connection, "Speed limit for all downloads in KB/s (0 = none)", s.SpeedLimitKBps.ToString());
        connection.Children.Add(new TextBlock { Text = "Proxy", Margin = new Thickness(0, 10, 0, 2) });
        var mode = new ComboBox { ItemsSource = Enum.GetValues<ProxyMode>().Select(ProxySettings.Title).ToList(), SelectedIndex = (int)s.Proxy.Mode };
        connection.Children.Add(mode);
        var host = Box(connection, "Proxy address", s.Proxy.Host);
        var port = Box(connection, "Port", s.Proxy.Port.ToString());
        var puser = Box(connection, "Proxy user name (optional)", s.Proxy.Username);
        connection.Children.Add(new TextBlock { Text = "Proxy password", Margin = new Thickness(0, 6, 0, 2) });
        var ppass = new PasswordBox { Password = s.Proxy.Password, Padding = new Thickness(4, 3, 4, 3) };
        connection.Children.Add(ppass);
        Hint(connection, "Proxy changes apply to downloads started afterwards. Password-protected downloads: add the user name and password in the Add window or under Properties.");
        connection.Children.Add(new TextBlock { Text = "Advanced", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0) });
        var net = s.Network;
        var minSeg = Box(connection, "Smallest part of a file in KB (64 or more)", net.MinSegmentKB.ToString());
        var retries = Box(connection, "Tries per part before a download stops", net.Retries.ToString());
        var connectTimeout = Box(connection, "Connection timeout in seconds", net.ConnectTimeoutSeconds.ToString());
        var readTimeout = Box(connection, "Reconnect a part that receives nothing for (seconds)", net.ReadTimeoutSeconds.ToString());
        var doh = Box(connection, "DNS over HTTPS server (empty = the system's DNS)", net.DnsOverHttps);
        Hint(connection, "For example " + string.Join(" or ", NetworkOptions.DohServers) + ". Not used through a proxy.");
        var checkResult = Hint(connection, "");
        var check = new Button { Content = "Check the Connection", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 6, 0, 0) };
        check.Click += async (_, _) =>
        {
            checkResult.Text = "Checking…";
            var keep = Network.Options;
            Network.Options = new NetworkOptions { ConnectTimeoutSeconds = Number(connectTimeout.Text, 30), DnsOverHttps = doh.Text }.Clamped();
            try
            {
                var proxy = new ProxySettings { Mode = (ProxyMode)Math.Max(0, mode.SelectedIndex), Host = host.Text.Trim(), Port = Number(port.Text, 8080), Username = puser.Text.Trim(), Password = ppass.Password };
                checkResult.Text = string.Join("\n", await NetworkCheck.RunAsync(proxy));
            }
            finally { Network.Options = keep; }
        };
        connection.Children.Add(check);

        var sites = Tab("Sites");
        Hint(sites, "Settings used for every download from a website: connections, speed limit, tries, timeout, user agent, folder, queue, login and proxy. " +
                    "A site also covers its subdomains (example.com covers www.example.com). Logins are only sent to that site and are saved encrypted.");
        var profiles = s.HostProfiles.Select(Clone).ToList();
        var siteList = new ListBox { Height = 200, Margin = new Thickness(0, 8, 0, 0) };
        void FillSites() => siteList.ItemsSource = profiles.Select(p => (p.Enabled ? "" : "(off) ") + HostProfiles.Normalize(p.Host) + Summary(p)).ToList();
        FillSites();
        sites.Children.Add(siteList);
        var siteButtons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        void SiteButton(string text, Action click)
        {
            var b = new Button { Content = text, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 6, 6) };
            b.Click += (_, _) => click();
            siteButtons.Children.Add(b);
        }
        void EditSelected()
        {
            var i = siteList.SelectedIndex;
            if (i >= 0 && EditSite(f.Window, Clone(profiles[i])) is { } p) { profiles[i] = p; FillSites(); }
        }
        SiteButton("Add Site…", () => { if (EditSite(f.Window, new HostProfile()) is { } p) { profiles.Add(p); FillSites(); } });
        SiteButton("Edit…", EditSelected);
        SiteButton("Remove", () =>
        {
            var i = siteList.SelectedIndex;
            if (i < 0 || MessageBox.Show(f.Window, $"Remove the settings for {HostProfiles.Normalize(profiles[i].Host)}?", "Remove Site",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            profiles.RemoveAt(i);
            FillSites();
        });
        sites.Children.Add(siteButtons);
        siteList.MouseDoubleClick += (_, _) => EditSelected();

        var backup = Tab("Backup");
        Hint(backup, "A backup holds your settings, site settings, queues and the downloads list. Passwords and cookies are never put in a backup.");
        var backupResult = Hint(backup, "");
        var backupButtons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var takeSettings = new CheckBox { Content = "When importing, also take the backup's settings", Margin = new Thickness(0, 8, 0, 0) };
        void BackupButton(string text, Action click)
        {
            var b = new Button { Content = text, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 6, 6) };
            b.Click += (_, _) => { try { click(); } catch (Exception e) { backupResult.Text = e.Message; } };
            backupButtons.Children.Add(b);
        }
        BackupButton("Export Backup…", () =>
        {
            var d = new Microsoft.Win32.SaveFileDialog { Title = "Export Backup", FileName = $"nexonic-backup-{DateTime.Now:yyyy-MM-dd}.json", Filter = "Backup (*.json)|*.json" };
            if (d.ShowDialog(f.Window) != true) return;
            File.WriteAllText(d.FileName, m.ExportBackup(HelpWindows.Version));
            backupResult.Text = $"Saved {Path.GetFileName(d.FileName)}: {m.Rows.Count} downloads, {m.Queues.Count} queues.";
        });
        BackupButton("Import Backup…", () =>
        {
            var d = new Microsoft.Win32.OpenFileDialog { Title = "Import Backup", Filter = "Backup (*.json)|*.json" };
            if (d.ShowDialog(f.Window) != true) return;
            var n = m.ImportBackup(File.ReadAllText(d.FileName), takeSettings.IsChecked == true);
            backupResult.Text = $"Added {n} download{(n == 1 ? "" : "s")}" + (takeSettings.IsChecked == true ? " and took the backup's settings." : ".");
            if (takeSettings.IsChecked == true) f.Close();
        });
        BackupButton("Export Links…", () =>
        {
            var d = new Microsoft.Win32.SaveFileDialog { Title = "Export Links", FileName = "nexonic-links.txt", Filter = "Text (*.txt)|*.txt" };
            if (d.ShowDialog(f.Window) != true) return;
            File.WriteAllLines(d.FileName, m.Rows.Select(r => r.Record.Url));
            backupResult.Text = $"Saved {m.Rows.Count} links to {Path.GetFileName(d.FileName)}.";
        });
        backup.Children.Add(backupButtons);
        backup.Children.Add(takeSettings);

        var clip = Tab("Clipboard");
        var watch = Tick(clip, "Watch the clipboard for copied download links", s.MonitorClipboard);
        var clipTypes = Box(clip, "File types", s.ClipboardTypes);
        clipTypes.TextWrapping = TextWrapping.Wrap;
        Hint(clip, "When you copy a link that ends in one of these types, the Add window opens with it.");

        var after = Tab("After Download");
        var complete = Tick(after, "Show the Download Complete window", s.ShowCompleteDialog);
        var sound = Tick(after, "Play a sound", s.PlaySound);
        var mark = Tick(after, "Mark files as downloaded from the internet so Windows checks them when opened", s.MarkDownloads);
        var scan = Tick(after, "Scan each finished file with a virus scanner", s.ScanEnabled);
        var scanCmd = Box(after, "Scanner command (the file is added at the end)", s.ScanCommand);
        Hint(after, "The default uses the virus scanner built into Windows. Expected checksums are checked automatically when a download finishes. Queues can also quit the app, sleep or shut down the computer when they are done (Scheduler).");

        var browsers = Tab("Browsers & Command Line");
        Hint(browsers, "Chrome, Edge, Brave, Vivaldi: open the browser's extensions page, switch on Developer mode, choose Load unpacked and pick the Extension folder in the app's folder.");
        var openFolder = new Button { Content = "Open the App Folder", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 6, 0, 0) };
        openFolder.Click += (_, _) => Process.Start("explorer.exe", $"\"{AppContext.BaseDirectory}\"");
        browsers.Children.Add(openFolder);
        Hint(browsers, "Firefox: drag \"Firefox Add-on.xpi\" from the app's folder onto a Firefox window. Firefox installs add-ons that are signed; see the README for signing your copy.");
        Hint(browsers, "Command line: open a new terminal and type ndm help. Example: ndm https://example.com/file.zip --queue Night");
        browsers.Children.Add(new TextBlock { Text = "Automation", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0) });
        var auto = s.Automation;
        var apiOn = Tick(browsers, "Let programs on this computer control downloads (local API)", auto.Enabled);
        var apiPort = Box(browsers, "Port", auto.Port.ToString());
        var apiKey = Box(browsers, "API key (programs send it as Authorization: Bearer <key>)", auto.Key);
        apiKey.IsReadOnly = true;
        var keyButtons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var newKey = new Button { Content = "New Key", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 6, 0) };
        newKey.Click += (_, _) => apiKey.Text = AutomationSettings.NewKey();
        var copyKey = new Button { Content = "Copy Key", Padding = new Thickness(12, 3, 12, 3) };
        copyKey.Click += (_, _) => { if (apiKey.Text.Length > 0) Clipboard.SetText(apiKey.Text); };
        keyButtons.Children.Add(newKey);
        keyButtons.Children.Add(copyKey);
        browsers.Children.Add(keyButtons);
        Hint(browsers, (m.ApiProblem.Length > 0 ? m.ApiProblem + " " : "") +
                       "Only this computer can connect (127.0.0.1), and only with the key. Add, pause, resume and remove downloads and start or stop queues; see docs/API.md. The key is saved encrypted and never in backups.");

        var error = f.Note("");
        error.Foreground = Brushes.Firebrick;
        f.AddButton("Cancel", f.Close, isCancel: true);
        f.AddButton("Save", () =>
        {
            var p = Number(port.Text, -1);
            if (p is < 1 or > 65535) { error.Text = "The proxy port must be between 1 and 65535."; return; }
            s.Connections = Math.Clamp(Number(conn.Text, 8), 1, 32);
            s.MaxActive = Math.Clamp(Number(active.Text, 3), 1, 10);
            s.Folder = folder.Text.Trim().Length > 0 ? folder.Text.Trim() : s.Folder;
            s.SortIntoCategoryFolders = sort.IsChecked == true;
            s.Notifications = notify.IsChecked == true;
            s.CheckForUpdates = updates.IsChecked == true;
            s.Language = languageChoices[Math.Max(0, language.SelectedIndex)];
            s.Theme = themeNames[Math.Max(0, themePicker.SelectedIndex)];
            s.ShowDownloadDialog = askDialog.IsChecked == true;
            s.ShowProgressWindow = progressWin.IsChecked == true;
            s.BringToFrontOnBrowserDownload = toFront.IsChecked == true;
            s.SpeedLimitKBps = Number(limit.Text);
            s.Proxy = new ProxySettings
            {
                Mode = (ProxyMode)Math.Max(0, mode.SelectedIndex), Host = host.Text.Trim(), Port = p,
                Username = puser.Text.Trim(), Password = ppass.Password,
            };
            s.MonitorClipboard = watch.IsChecked == true;
            s.ClipboardTypes = clipTypes.Text;
            s.ShowCompleteDialog = complete.IsChecked == true;
            s.PlaySound = sound.IsChecked == true;
            s.MarkDownloads = mark.IsChecked == true;
            s.ScanEnabled = scan.IsChecked == true;
            s.ScanCommand = scanCmd.Text.Trim();
            if (!string.IsNullOrWhiteSpace(doh.Text) && !NetworkOptions.DnsOverHttpsUrlOk(doh.Text)) { error.Text = "The DNS over HTTPS server must be an https:// address."; return; }
            s.Network = new NetworkOptions
            {
                MinSegmentKB = Number(minSeg.Text, 512), Retries = Number(retries.Text, 6),
                ConnectTimeoutSeconds = Number(connectTimeout.Text, 30), ReadTimeoutSeconds = Number(readTimeout.Text, 60), DnsOverHttps = doh.Text,
            }.Clamped();
            s.HostProfiles = profiles;
            var apiPortNumber = Number(apiPort.Text, -1);
            if (apiOn.IsChecked == true && apiPortNumber is < 1024 or > 65535) { error.Text = "The automation port must be between 1024 and 65535."; return; }
            s.Automation = new AutomationSettings
            {
                Enabled = apiOn.IsChecked == true, Port = apiPortNumber is >= 1024 and <= 65535 ? apiPortNumber : 47812,
                Key = apiKey.Text.Length >= 16 ? apiKey.Text : apiOn.IsChecked == true ? AutomationSettings.NewKey() : "",
            };
            m.ApplySettings(s);
            ThemeManager.Apply(s.Theme);
            m.ApplyAutomation();
            f.Close();
        }, isDefault: true);
        f.Show();
    }

    static HostProfile Clone(HostProfile p) => System.Text.Json.JsonSerializer.Deserialize<HostProfile>(System.Text.Json.JsonSerializer.Serialize(p))!;

    static string Summary(HostProfile p)
    {
        var parts = new List<string>();
        if (p.MaxConnections is int c) parts.Add($"{c} connections");
        if (p.SpeedLimitKBps is int l and > 0) parts.Add($"{l} KB/s");
        if (!string.IsNullOrEmpty(p.Folder)) parts.Add("own folder");
        if (!string.IsNullOrEmpty(p.Username)) parts.Add("login");
        if (p.Proxy != null) parts.Add("own proxy");
        return parts.Count > 0 ? "   ·   " + string.Join(", ", parts) : "";
    }

    static int? Optional(string text, int min, int max) => int.TryParse(text.Trim(), out var n) ? Math.Clamp(n, min, max) : null;

    /// <summary>Edits one site's settings; null when cancelled.</summary>
    static HostProfile? EditSite(Window owner, HostProfile p)
    {
        HostProfile? result = null;
        var f = new Form(owner, "Site Settings", 560);
        f.Note("Leave a field empty to use the general setting.");
        var host = f.Text("Website (for example example.com)", p.Host);
        var enabled = f.Check("Use these settings", p.Enabled);
        var conns = f.Text("Connections per download (1-32)", p.MaxConnections?.ToString() ?? "");
        var limit = f.Text("Speed limit in KB/s (0 = none)", p.SpeedLimitKBps?.ToString() ?? "");
        var tries = f.Text("Tries per part (1-50)", p.Retries?.ToString() ?? "");
        var timeout = f.Text("Reconnect after this many silent seconds", p.TimeoutSeconds?.ToString() ?? "");
        var ua = f.Text("User agent (how the app introduces itself to the site)", p.UserAgent ?? "");
        var folder = f.Folder(p.Folder ?? "");
        var queue = f.Text("Queue (name; empty = the one chosen when adding)", p.QueueName ?? "");
        var later = f.Check("Add downloads from this site stopped (start them yourself or with a queue)", p.StartAtOnce == false);
        var user = f.Text("User name", p.Username ?? "");
        var pass = f.Field("Password", new PasswordBox { Password = p.Password ?? "", Padding = new Thickness(4, 3, 4, 3) });
        var ownProxy = f.Check("Use its own proxy", p.Proxy != null);
        var proxyMode = f.Field("Proxy", new ComboBox { ItemsSource = Enum.GetValues<ProxyMode>().Select(ProxySettings.Title).ToList(), SelectedIndex = (int)(p.Proxy?.Mode ?? ProxyMode.None) });
        var proxyHost = f.Text("Proxy address", p.Proxy?.Host ?? "");
        var proxyPort = f.Text("Port", (p.Proxy?.Port ?? 8080).ToString());
        var error = f.Note("");
        error.Foreground = Brushes.Firebrick;
        f.AddButton("Cancel", f.Close, isCancel: true);
        f.AddButton("OK", () =>
        {
            var h = HostProfiles.Normalize(host.Text);
            if (h.Length == 0 || Uri.CheckHostName(h) == UriHostNameType.Unknown) { error.Text = "Enter the website's address, for example example.com."; return; }
            result = new HostProfile
            {
                Host = h, Enabled = enabled.IsChecked == true,
                MaxConnections = Optional(conns.Text, 1, 32), SpeedLimitKBps = Optional(limit.Text, 0, 10_000_000),
                Retries = Optional(tries.Text, 1, 50), TimeoutSeconds = Optional(timeout.Text, 5, 3600),
                UserAgent = string.IsNullOrWhiteSpace(ua.Text) ? null : ua.Text.Trim(),
                Folder = string.IsNullOrWhiteSpace(folder.Text) ? null : folder.Text.Trim(),
                QueueName = string.IsNullOrWhiteSpace(queue.Text) ? null : queue.Text.Trim(),
                StartAtOnce = later.IsChecked == true ? false : null,
                Username = string.IsNullOrWhiteSpace(user.Text) ? null : user.Text.Trim(),
                Password = string.IsNullOrEmpty(pass.Password) ? null : pass.Password,
                Proxy = ownProxy.IsChecked == true
                    ? new ProxySettings { Mode = (ProxyMode)Math.Max(0, proxyMode.SelectedIndex), Host = proxyHost.Text.Trim(), Port = Number(proxyPort.Text, 8080), Password = p.Proxy?.Password ?? "", Username = p.Proxy?.Username ?? "" }
                    : null,
            };
            f.Close();
        }, isDefault: true);
        f.Show();
        return result;
    }

    // ---- QR code ----

    /// <summary>The downloads as a QR code: the phone's camera opens them in Nexonic on Android or iPhone.</summary>
    public static void ShowQr(Window owner, List<string> urls)
    {
        var (link, count) = ShareLinks.Link(urls);
        if (count == 0) return;
        var qr = QrCode.Encode(link);
        var geometry = new GeometryGroup();
        for (var y = 0; y < qr.Size; y++)
            for (var x = 0; x < qr.Size; x++)
                if (qr[x, y]) geometry.Children.Add(new RectangleGeometry(new Rect(x, y, 1, 1)));
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(-4, -4, qr.Size + 8, qr.Size + 8))));
        drawing.Children.Add(new GeometryDrawing(Brushes.Black, null, geometry));
        var image = new Image { Source = new DrawingImage(drawing), Width = 300, Height = 300, Margin = new Thickness(0, 4, 0, 8) };
        RenderOptions.SetEdgeMode(image, EdgeMode.Aliased);
        var f = new Form(owner, "Download on Your Phone", 400);
        f.Body.Children.Add(image);
        f.Note(count == 1 ? "Scan with your phone's camera to download this file with the Nexonic app on your phone."
                          : $"Scan with your phone's camera to download these {count} files with the Nexonic app on your phone.");
        if (count < urls.Count) f.Note($"Only the first {count} of {urls.Count} links fit in one code.");
        if (urls.Any(ShareLinks.LooksPrivate))
            f.Note("This link seems to include a private access key (for example a token or signature). Anyone who scans the code can use it until it expires, so share it only with your own phone.");
        f.Note("Only the links are shared: no cookies or passwords. Downloads that need you to be signed in may not work on the phone.");
        f.AddButton("Close", f.Close, isDefault: true, isCancel: true);
        f.Show();
    }

    // ---- Download complete ----

    public static void Complete(DownloadManager m, DownloadRecord r)
    {
        var w = new Window
        {
            Title = "Download Complete", Width = 460, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true, ShowActivated = true,
        };
        var p = new StackPanel { Margin = new Thickness(16) };
        p.Children.Add(new TextBlock { Text = r.FileName, FontWeight = FontWeights.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
        p.Children.Add(new TextBlock
        {
            Text = $"{(r.Size is long s ? Row.Bytes(s) : "")} saved to {r.SavedPath}", Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        });
        if (r.ChecksumResult is { } c)
            p.Children.Add(new TextBlock { Text = c, Foreground = c.Contains("not") ? Brushes.Firebrick : Brushes.SeaGreen, Margin = new Thickness(0, 4, 0, 0) });
        var never = new CheckBox { Content = "Don't show this again", Margin = new Thickness(0, 10, 0, 0) };
        p.Children.Add(never);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        void Close()
        {
            if (never.IsChecked == true)
            {
                var st = m.Settings;
                st.ShowCompleteDialog = false;
                m.ApplySettings(st);
            }
            w.Close();
        }
        Button B(string text, Action a, bool def = false, bool cancel = false)
        {
            var b = new Button { Content = text, MinWidth = 90, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0), IsDefault = def, IsCancel = cancel };
            b.Click += (_, _) => a();
            buttons.Children.Add(b);
            return b;
        }
        B("Close", Close, cancel: true);
        B("Open Folder", () => { if (r.SavedPath is { } path) Process.Start("explorer.exe", $"/select,\"{path}\""); Close(); });
        B("Open", () => { if (r.SavedPath is { } path) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); Close(); }, def: true);
        p.Children.Add(buttons);
        w.Content = p;
        w.Show();
    }
}
