using System.Diagnostics;
using System.IO;
using Path = System.IO.Path;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Nexonic.Core;

namespace Nexonic.App;

/// <summary>
/// The download window (shown when a link is clicked in the browser) and one progress window per download,
/// built on the same manager as the main window.
/// </summary>
public static class DownloadWindows
{
    static readonly Dictionary<string, Window> infoWindows = new();   // by link: one link never gets two windows
    static readonly Dictionary<Guid, Window> progressWindows = new();
    static readonly Brush Muted = Brushes.DimGray;

    static Window? Owner => Application.Current.MainWindow is { IsVisible: true } w ? w : null;

    static Button Btn(Panel p, string text, Action click, bool def = false, bool cancel = false)
    {
        var b = new Button { Content = text, MinWidth = 96, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(6, 0, 0, 0), IsDefault = def, IsCancel = cancel };
        b.Click += (_, _) => click();
        p.Children.Add(b);
        return b;
    }

    static TextBlock Caption(string text) => new() { Text = text, Foreground = Muted, Margin = new Thickness(0, 3, 14, 3), VerticalAlignment = VerticalAlignment.Center };

    static void Place(Window w, IEnumerable<Window> others)
    {
        if (others.LastOrDefault(o => o.IsVisible) is { } last)
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = last.Left + 28;
            w.Top = last.Top + 28;
        }
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    // ---- Download File Info ----

    public static void ShowInfo(DownloadManager m, DownloadJob job)
    {
        job = job with { Url = Links.Unwrap(job.Url) };  // e.g. a Google Images result: the picture itself
        if (infoWindows.TryGetValue(job.Url, out var open)) { open.Activate(); return; }
        var uri = new Uri(job.Url);
        var site = job.Stream != true && (job.Site == true || Nexonic.Core.SiteVideo.IsSupported(job.Url));
        var stream = !site && (job.Stream == true || HLS.IsPlaylistUrl(job.Url));
        var w = new Window
        {
            Title = "Download File Info", Width = 560, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            Topmost = true, ShowActivated = true, ShowInTaskbar = true,
        };
        Place(w, infoWindows.Values);
        var root = new StackPanel { Margin = new Thickness(18) };

        var head = new DockPanel();
        var name = new TextBox
        {
            Text = stream ? HLS.PendingFileName(job.FileName is { Length: > 0 } streamName ? FileNames.Sanitize(streamName) : FileNames.FromUrl(job.Url))
                : job.FileName is { Length: > 0 } siteName ? FileNames.Sanitize(siteName) : site ? $"Media from {uri.Host}" : FileNames.FromUrl(job.Url),
            FontSize = 15, FontWeight = FontWeights.SemiBold, Padding = new Thickness(4, 3, 4, 3),
        };
        var icon = new Image { Width = 40, Height = 40, Margin = new Thickness(0, 0, 12, 0), Source = FileIcon.For(name.Text) };
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        var titles = new StackPanel();
        titles.Children.Add(name);
        titles.Children.Add(new TextBlock { Text = job.Url, Foreground = Muted, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 4, 0, 0), ToolTip = job.Url });
        head.Children.Add(titles);
        root.Children.Add(head);

        var grid = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        int rowIndex = 0;
        void AddRow(string label, UIElement value)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = Caption(label);
            Grid.SetRow(l, rowIndex);
            Grid.SetRow(value, rowIndex);
            Grid.SetColumn(value, 1);
            grid.Children.Add(l);
            grid.Children.Add(value);
            rowIndex++;
        }
        var size = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Text = job.Size is long s ? Nexonic.App.Row.Bytes(s) : stream ? "HLS stream · size calculated during download" : site ? "Finding the qualities on offer…" : "Checking…" };
        AddRow("Size", size);
        var category = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Text = stream ? "Detecting after download" : FileCategories.Of(name.Text).ToString() };
        AddRow("Category", category);
        name.TextChanged += (_, _) => { category.Text = stream ? "Detecting after download" : FileCategories.Of(name.Text).ToString(); icon.Source = FileIcon.For(name.Text); };
        if (stream) AddRow("Output format", new TextBlock { Text = "Automatically detected from audio/video streams", VerticalAlignment = VerticalAlignment.Center });
        var folder = new TextBox { Text = m.Settings.Folder, Padding = new Thickness(4, 3, 4, 3) };
        var pick = new Button { Content = "Choose…", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };
        pick.Click += (_, _) =>
        {
            var d = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(folder.Text) ? folder.Text : "" };
            if (d.ShowDialog(w) == true) folder.Text = d.FolderName;
        };
        var folderRow = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        DockPanel.SetDock(pick, Dock.Right);
        folderRow.Children.Add(pick);
        folderRow.Children.Add(folder);
        AddRow("Save to", folderRow);
        var queue = new ComboBox { Margin = new Thickness(0, 3, 0, 3), HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 200 };
        foreach (var q in m.Queues)
            queue.Items.Add(new ComboBoxItem { Content = m.QueueName(q.Id), Tag = q.Id });
        queue.SelectedItem = queue.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (job.Queue ?? DownloadQueue.MainId))
            ?? queue.Items.Cast<ComboBoxItem>().FirstOrDefault();
        AddRow("Queue", queue);
        // Quality: 0 = best, -1 = sound only, otherwise the highest picture height. Filled in when the video is looked up.
        var quality = new ComboBox { Margin = new Thickness(0, 3, 0, 3), HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 200 };
        SiteVideo.Choices? offered = null;
        long? bestSize = null;
        var looked = false;
        bool autoAudio = false;
        int Chosen() => quality.SelectedItem is ComboBoxItem { Tag: int t } ? t : 0;
        void ShowSize()
        {
            if (!looked) return;
            var q = Chosen();
            long? known = q == 0 ? bestSize : q == -1 ? offered?.SoundSize : offered?.Video.FirstOrDefault(v => v.Height == q)?.Size;
            size.Text = known is long bytes ? Nexonic.App.Row.Bytes(bytes) : "Size known when the download starts";
        }
        void FillQualities(int keep)
        {
            var first = offered?.Video.FirstOrDefault();
            if (keep > 0 && keep == first?.Height) keep = 0;  // that is the best on offer
            var lower = offered?.Video.Skip(1).Select(q => q.Height).ToList() ?? new List<int>();
            if (keep > 0 && !lower.Contains(keep)) lower.Add(keep);
            quality.Items.Clear();
            quality.Items.Add(new ComboBoxItem { Content = first != null ? $"Best ({SiteVideo.QualityName(first.Height)})" : "Best available", Tag = 0 });
            foreach (var h in lower.OrderByDescending(x => x)) quality.Items.Add(new ComboBoxItem { Content = SiteVideo.QualityName(h), Tag = h });
            quality.Items.Add(new ComboBoxItem { Content = "Sound only", Tag = -1 });
            quality.SelectedItem = quality.Items.Cast<ComboBoxItem>().First(i => (int)i.Tag == keep);
        }
        FillQualities(job.AudioOnly == true ? -1 : job.MaxHeight ?? 0);
        quality.SelectionChanged += (_, _) => ShowSize();
        if (site) AddRow("Quality", quality);
        root.Children.Add(grid);

        var never = new CheckBox { Content = "Don't show this window; start browser downloads right away", Margin = new Thickness(0, 12, 0, 0), FontSize = 11 };
        root.Children.Add(never);

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        string? infoPath = null;
        void Done(bool? start)
        {
            if (never.IsChecked == true)
            {
                var st = m.Settings;
                st.ShowDownloadDialog = false;
                m.ApplySettings(st);
            }
            if (start is bool go)
            {
                var o = new AddOptions
                {
                    Folder = folder.Text.Trim().Length > 0 ? folder.Text.Trim() : null,
                    QueueId = (queue.SelectedItem as ComboBoxItem)?.Tag as string ?? DownloadQueue.MainId,
                    Start = go,
                    AudioOnly = Chosen() == -1,
                };
                var id = m.Add(job with
                {
                    FileName = name.Text.Trim().Length > 0 ? name.Text.Trim() : null, Start = go, InfoJson = infoPath,
                    AudioOnly = Chosen() == -1, MaxHeight = Chosen() > 0 ? Chosen() : null,
                }, o);
                if (go && id is Guid g && m.Settings.ShowProgressWindow && m.Rows.FirstOrDefault(r => r.Record.Id == g) is { } row)
                    ShowProgress(m, row);
            }
            w.Close();
        }
        Btn(buttons, "Cancel", () => Done(null), cancel: true);
        Btn(buttons, "Download Later", () => Done(false));
        Btn(buttons, "Start Download", () => Done(true), def: true);
        root.Children.Add(buttons);
        w.Content = root;
        infoWindows[job.Url] = w;
        w.Closed += (_, _) => infoWindows.Remove(job.Url);
        w.Show();
        w.Activate();

        if (site) _ = LookUpVideoAsync(job, size, name, (path, found, total) =>
        {
            infoPath = path;
            offered = found;
            // No video renditions: classify as audio automatically, regardless of host.
            // Do not override a user's manual quality selection.
            if (found is { Video.Count: 0 } && !autoAudio && Chosen() == 0)
            {
                autoAudio = true;
                FillQualities(-1);
            }
            bestSize = total ?? found?.Video.FirstOrDefault()?.Size;  // estimated from the bitrate when not given
            looked = true;
            FillQualities(Chosen());
            ShowSize();
        }, m.Settings.Proxy);
        else if (job.Size is null && job.Stream != true) _ = ProbeAsync(m, job, size, name);
    }

    /// <summary>
    /// Looks the video up while the window is open (title, size, best quality). The details are saved and reused, so the
    /// download starts at once instead of looking the video up again.
    /// </summary>
    static async Task LookUpVideoAsync(DownloadJob job, TextBlock size, TextBox name, Action<string, SiteVideo.Choices?, long?> found,
        ProxySettings proxy)
    {
        size.Text = "Finding the qualities on offer…";
        var path = Path.Combine(AppPaths.Parts, $"info-{Guid.NewGuid():N}.json");
        var result = await Task.Run(() =>
        {
            try
            {
                SiteVideoTools.Prepare();
                if (!File.Exists(SiteVideoTools.YtDlp)) return ((SiteVideo.Info?)null, "");
                Directory.CreateDirectory(AppPaths.Parts);
                var psi = new ProcessStartInfo(SiteVideoTools.YtDlp)
                {
                    CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                };
                psi.Environment["PYTHONIOENCODING"] = "utf-8";
                var (args, configFile) = SiteVideo.Prepare(
                    SiteVideo.InfoArguments(job.Url, SiteVideoTools.QuickJs, false, job.Referrer, job.Cookies, job.UserAgent, null, null), proxy);
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var cleanup = new ConfigCleanup(configFile);
                using var p = Process.Start(psi)!;
                p.ErrorDataReceived += (_, _) => { };
                p.BeginErrorReadLine();
                var json = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                var info = p.ExitCode == 0 ? SiteVideo.ParseInfo(json) : null;
                if (info != null) File.WriteAllText(path, json);
                return (info, json);
            }
            catch { return ((SiteVideo.Info?)null, ""); }
        });
        if (result.Item1 is not { } info)
        {
            size.Text = "Size known when the download starts";
            return;
        }
        found(path, SiteVideo.ParseChoices(result.Item2), info.Size);
        if (string.IsNullOrEmpty(job.FileName))
        {
            // The extractor's pre-download ext describes its source, not necessarily
            // the final seekable container. Avoid advertising .mp4 for audio-only media.
            var choices = SiteVideo.ParseChoices(result.Item2);
            name.Text = choices is { Video.Count: 0 }
                ? FileNames.Sanitize(info.Title)
                : FileNames.Sanitize($"{info.Title}.{info.Ext}");
        }
    }

    /// <summary>Asks the server for the size (and the real file name) without downloading the file.</summary>
    static async Task ProbeAsync(DownloadManager m, DownloadJob job, TextBlock size, TextBox name)
    {
        try
        {
            var handler = Network.Handler(m.Settings.Proxy);
            handler.AllowAutoRedirect = true;
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using var req = new HttpRequestMessage(HttpMethod.Head, job.Url);
            if (!string.IsNullOrEmpty(job.Cookies)) req.Headers.TryAddWithoutValidation("Cookie", job.Cookies);
            if (!string.IsNullOrEmpty(job.UserAgent)) req.Headers.TryAddWithoutValidation("User-Agent", job.UserAgent);
            if (!string.IsNullOrEmpty(job.Referrer)) req.Headers.TryAddWithoutValidation("Referer", job.Referrer);
            using var res = await http.SendAsync(req);
            var length = res.Content.Headers.ContentLength;
            size.Text = length is > 0 ? Nexonic.App.Row.Bytes(length.Value) : "Unknown";
            var suggested = res.Content.Headers.ContentDisposition?.FileNameStar ?? res.Content.Headers.ContentDisposition?.FileName;
            if (string.IsNullOrEmpty(job.FileName) && !string.IsNullOrWhiteSpace(suggested))
                name.Text = FileNames.Sanitize(suggested.Trim('"'));
        }
        catch { size.Text = "Unknown"; }
    }

    // ---- Progress window ----

    public static void ShowProgress(DownloadManager m, Row row)
    {
        var id = row.Record.Id;
        if (progressWindows.TryGetValue(id, out var open))
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        var w = new Window { Width = 580, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.CanMinimize, ShowActivated = false };
        Place(w, progressWindows.Values);
        var root = new StackPanel { Margin = new Thickness(16) };

        var head = new DockPanel();
        var icon = new Image { Width = 36, Height = 36, Margin = new Thickness(0, 0, 12, 0), Source = FileIcon.For(row.Record.FileName) };
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        var titles = new StackPanel();
        var fileName = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        titles.Children.Add(fileName);
        titles.Children.Add(new TextBlock { Text = row.Record.Url, Foreground = Muted, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = row.Record.Url });
        head.Children.Add(titles);
        root.Children.Add(head);

        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var values = new List<TextBlock>();
        foreach (var label in new[] { "Status", "File size", "Downloaded", "Transfer rate", "Time left", "Resume capability" })
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = Caption(label);
            var v = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 0, 3) };
            Grid.SetRow(l, values.Count);
            Grid.SetRow(v, values.Count);
            Grid.SetColumn(v, 1);
            grid.Children.Add(l);
            grid.Children.Add(v);
            values.Add(v);
        }
        root.Children.Add(grid);

        var bar = new ProgressBar { Height = 14, Margin = new Thickness(0, 12, 0, 0), Maximum = 100, Foreground = Theme.Good };
        root.Children.Add(bar);

        // Speed graph
        var graph = new Canvas { Height = 46, Margin = new Thickness(0, 10, 0, 0), Background = new SolidColorBrush(Color.FromArgb(0x12, 0x80, 0x80, 0x80)), ClipToBounds = true };
        var area = new Polygon { Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x1F, 0x6F, 0xEB)) };
        var line = new Polyline { Stroke = Theme.Accent, StrokeThickness = 1.5 };
        graph.Children.Add(area);
        graph.Children.Add(line);
        root.Children.Add(graph);

        var details = new StackPanel();
        var segCaption = new TextBlock { Text = "Start positions and progress by connection", Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 10, 0, 4) };
        var segments = new Canvas { Height = 12, Background = new SolidColorBrush(Color.FromRgb(0xE3, 0xE8, 0xEF)), ClipToBounds = true };
        var conns = new ListView { MaxHeight = 150, Margin = new Thickness(0, 8, 0, 0) };
        var gv = new GridView();
        gv.Columns.Add(new GridViewColumn { Header = "N.", Width = 44, DisplayMemberBinding = new System.Windows.Data.Binding("N") });
        gv.Columns.Add(new GridViewColumn { Header = "Downloaded", Width = 130, DisplayMemberBinding = new System.Windows.Data.Binding("Downloaded") });
        gv.Columns.Add(new GridViewColumn { Header = "Info", Width = 300, DisplayMemberBinding = new System.Windows.Data.Binding("Info") });
        conns.View = gv;
        details.Children.Add(segCaption);
        details.Children.Add(segments);
        details.Children.Add(conns);
        root.Children.Add(details);

        var error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        root.Children.Add(error);

        var buttons = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        var toggle = new Button { Content = "<< Hide details", Padding = new Thickness(12, 5, 12, 5) };
        toggle.Click += (_, _) =>
        {
            details.Visibility = details.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            toggle.Content = details.Visibility == Visibility.Visible ? "<< Hide details" : "Show details >>";
        };
        DockPanel.SetDock(toggle, Dock.Left);
        buttons.Children.Add(toggle);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(right, Dock.Right);
        var pause = Btn(right, "Pause", () =>
        {
            if (row.Record.IsActive || row.Record.Status == DownloadStatus.Queued) m.Pause(row); else m.Resume(row);
        });
        Btn(right, "Cancel", () =>
        {
            var what = row.Record.Torrent != null ? $"{row.Record.FileName} is removed from the list. Torrent files stay on disk."
                : $"The part of {row.Record.FileName} already downloaded is deleted. It stays in the list as Canceled; Resume starts it again.";
            if (ThemedMessageBox.Show(w, what, "Cancel this download?", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            w.Close();
            m.Cancel(row);
        });
        Btn(right, "Close", w.Close, cancel: true);
        buttons.Children.Add(right);
        root.Children.Add(buttons);
        w.Content = root;

        var history = new List<double>();
        var lastSegments = "";
        void Update()
        {
            var r = row.Record;
            if (!m.Rows.Contains(row)) { w.Close(); return; }
            if (r.Status == DownloadStatus.Finished) { w.Close(); return; }
            var pct = r.Progress * 100;
            w.Title = $"{(int)pct}% {r.FileName}";
            fileName.Text = r.FileName;
            values[0].Text = row.PreparingVideo ? "Preparing video… (finding the best quality)"
                : r.IsActive && r.VideoStep is { } videoStep ? videoStep : r.Status switch
            {
                DownloadStatus.Queued => "Waiting in queue",
                DownloadStatus.Probing => "Connecting…",
                DownloadStatus.Downloading => "Receiving data…",
                DownloadStatus.Paused => "Paused",
                DownloadStatus.Failed => r.IsCanceled ? "Canceled" : "Failed",
                _ => "Complete",
            };
            values[1].Text = r.Size is long s ? Nexonic.App.Row.Bytes(s) : "Unknown";
            values[2].Text = Nexonic.App.Row.Bytes(r.Received) + (r.Size is > 0 ? $" ({pct:0.0} %)" : "");
            values[3].Text = r.IsActive && row.SpeedBytes > 0 ? row.Speed : "–";
            values[4].Text = row.TimeLeft.Length > 0 ? row.TimeLeft : "–";
            values[5].Text = r.AcceptsRanges || r.SiteVideo == true || r.IsStream == true ? "Yes" : "No";
            bar.IsIndeterminate = r.IsActive && r.Size is null && r.IsStream != true;
            bar.Value = pct;
            pause.Content = r.IsActive || r.Status == DownloadStatus.Queued ? "Pause" : "Resume";
            error.Text = r.Error ?? "";
            error.Visibility = string.IsNullOrEmpty(r.Error) ? Visibility.Collapsed : Visibility.Visible;

            history.Add(r.IsActive ? row.SpeedBytes : 0);
            if (history.Count > 120) history.RemoveAt(0);
            var width = graph.ActualWidth > 0 ? graph.ActualWidth : 540;
            var height = graph.Height;
            var top = history.Count > 0 ? history.Max() : 0;
            var pts = new PointCollection();
            if (top > 0 && history.Count > 1)
            {
                var step = width / (history.Count - 1);
                for (var i = 0; i < history.Count; i++) pts.Add(new Point(i * step, height - history[i] / top * (height - 4)));
            }
            line.Points = pts;
            var poly = new PointCollection(pts);
            if (pts.Count > 1) { poly.Add(new Point(pts[^1].X, height)); poly.Add(new Point(0, height)); }
            area.Points = poly;

            var plain = r.IsStream != true && r.SiteVideo != true && r.Segments.Count > 0;
            segments.Visibility = plain ? Visibility.Visible : Visibility.Collapsed;
            conns.Visibility = plain || r.SiteVideo == true ? Visibility.Visible : Visibility.Collapsed;
            if (r.SiteVideo == true)
            {
                // The picture and sound pieces, like the connection list of a normal download.
                var pieces = r.VideoParts?.ToList() ?? new List<VideoPart> { new() { Name = "Video", Done = r.Received } };
                var videoKey = string.Join(",", pieces.Select(x => x.Done)) + r.Status + r.VideoStep;
                if (videoKey != lastSegments)
                {
                    lastSegments = videoKey;
                    conns.ItemsSource = pieces.Select((pc, i) => new
                    {
                        N = i + 1,
                        Downloaded = Nexonic.App.Row.Bytes(pc.Done) + (pc.Total is long t ? " of " + Nexonic.App.Row.Bytes(t) : ""),
                        Info = pc.Name + " · " + (pc.Total is long tt && pc.Done >= tt ? "Complete"
                            : r.Status == DownloadStatus.Downloading ? r.VideoStep ?? "Receiving data…"
                            : r.Status == DownloadStatus.Probing ? "Connecting…" : "Waiting"),
                    }).ToList();
                }
            }
            if (!plain)
            {
                segCaption.Text = r.SiteVideo == true
                    ? ("Media site · " + SiteVideo.ChoiceText(r.AudioOnly, r.MaxHeight)) + $"\nSave to: {r.Folder}"
                    : r.IsStream == true ? $"HLS media · pieces: {r.StreamDone ?? 0} of {r.StreamTotal ?? 0} · format detected automatically" : "";
                segCaption.Visibility = segCaption.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                return;
            }
            segCaption.Text = "Start positions and progress by connection";
            segCaption.Visibility = Visibility.Visible;
            List<Segment> segs;
            lock (r.Segments) segs = r.Segments.Select(x => new Segment { Id = x.Id, Start = x.Start, End = x.End, Written = x.Written }).ToList();
            var key = string.Join(",", segs.Select(x => x.Written)) + r.Status;
            if (key == lastSegments) return;
            lastSegments = key;
            segments.Children.Clear();
            var segWidth = segments.ActualWidth > 0 ? segments.ActualWidth : 540;
            if (r.Size is > 0)
                foreach (var sg in segs)
                {
                    var rect = new Rectangle { Height = segments.Height, Width = Math.Max(0.5, segWidth * sg.Written / r.Size.Value), Fill = sg.IsComplete ? Theme.Good : Theme.Accent };
                    Canvas.SetLeft(rect, segWidth * sg.Start / r.Size.Value);
                    segments.Children.Add(rect);
                }
            conns.ItemsSource = segs.Select(sg => new
            {
                N = sg.Id + 1,
                Downloaded = Nexonic.App.Row.Bytes(sg.Written),
                Info = sg.IsComplete ? "Complete" : r.Status == DownloadStatus.Downloading ? "Receiving data…" : r.Status == DownloadStatus.Probing ? "Connecting…" : "Waiting",
            }).ToList();
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => Update();
        progressWindows[id] = w;
        w.Closed += (_, _) => { timer.Stop(); progressWindows.Remove(id); };
        Update();
        if (!progressWindows.ContainsKey(id)) return;   // finished or removed already
        w.Show();
        timer.Start();
    }
}
