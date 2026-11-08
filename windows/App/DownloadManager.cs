using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using Nexonic.Core;

namespace Nexonic.App;

public sealed class AddOptions
{
    public string? Folder { get; set; }
    public string? FileName { get; set; }
    public string QueueId { get; set; } = DownloadQueue.MainId;
    public bool Start { get; set; } = true;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Checksum { get; set; } = "";
    public int SpeedLimitKBps { get; set; }
    public bool AudioOnly { get; set; }
    public int? MaxHeight { get; set; }    // a single video link's highest picture quality (null = best)
    public string? InfoJson { get; set; }  // a single video link's details, already looked up in the Add window
}

/// <summary>Queues, scheduler, persistence and after-download actions (UI thread only).</summary>
public sealed partial class DownloadManager
{
    public ObservableCollection<Row> Rows { get; } = new();
    public ObservableCollection<DownloadQueue> Queues { get; } = new();
    public AppSettings Settings { get; private set; }
    public event Action<DownloadRecord>? Finished;
    public event Action<string, string>? Notify;
    public event Action<string>? ClipboardLink;
    public event Action? QuitRequested;
    public void RequestQuit() => QuitRequested?.Invoke();
    /// <summary>A link clicked in the browser that should open the download window.</summary>
    public event Action<DownloadJob>? DownloadDialogRequested;
    public event Action<Row>? ProgressRequested;
    public event Action? ShowRequested;

    readonly Dictionary<Guid, IDownloader> transfers = new();
    readonly HashSet<string> queuesWithWork = new();
    readonly SpeedLimiter limiter = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    uint clipboardSequence = GetClipboardSequenceNumber();
    string lastMinute = "";
    int ticks;

    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();

    public DownloadManager()
    {
        Settings = Load<AppSettings>(AppPaths.Settings) ?? new AppSettings();
        foreach (var q in Load<List<DownloadQueue>>(AppPaths.Queues) ?? new()) Queues.Add(q);
        if (!Queues.Any(q => q.IsMain)) Queues.Insert(0, DownloadQueue.Main());
        foreach (var r in Load<List<DownloadRecord>>(AppPaths.Downloads) ?? new())
        {
            if (r.IsActive) r.Status = DownloadStatus.Queued;
            Rows.Add(new Row(r, QueueName));
        }
        // Lists and settings saved by 0.6.1 hold cookies and passwords in plain text: encrypt them now.
        if (File.Exists(AppPaths.Settings)) Write(AppPaths.Settings, Settings);
        if (File.Exists(AppPaths.Downloads)) Save();
        limiter.SetLimit(Settings.SpeedLimitKBps * 1024);
        Network.Options = Settings.Network.Clamped();
        timer.Tick += (_, _) => Tick();
        timer.Start();
        CollectInbox();
        Schedule();
        Task.Run(SiteVideoTools.Prepare); // keeps the video engine current
    }

    public void ApplySettings(AppSettings s)
    {
        Settings = s;
        limiter.SetLimit(s.SpeedLimitKBps * 1024);
        Network.Options = s.Network.Clamped();
        if (TorrentRuntime.Unavailable(s) != null)
            foreach (var row in Rows.Where(r => r.Record.Torrent != null && r.Record.IsActive).ToList()) Pause(row);
        Write(AppPaths.Settings, s);
        Schedule();
    }

    /// <summary>Sample list for the README screenshots (only used when the app is started with NDM_DEMO=1).</summary>
    public void LoadDemo(List<DownloadRecord> records, DownloadQueue extra)
    {
        Rows.Clear();
        foreach (var r in records.AsEnumerable().Reverse()) Rows.Add(new Row(r, QueueName));
        Queues.Add(extra);
        Schedule();
    }

    // ---- adding ----

    public int AddLinks(string text, AddOptions? o = null)
    {
        o ??= new AddOptions();
        var lines = Links.Split(text);
        var n = 0;
        foreach (var line in lines)
        {
            var job = new DownloadJob(line, lines.Count == 1 && !string.IsNullOrWhiteSpace(o.FileName) ? o.FileName : null);
            if (lines.Count == 1) job = job with { InfoJson = o.InfoJson, MaxHeight = o.MaxHeight };
            if (job.IsValid) { Add(job, o); n++; }
        }
        return n;
    }

    public Guid? Add(DownloadJob job, AddOptions? o = null)
    {
        if (!job.IsValid) return null;
        job = job with { Url = Links.Unwrap(job.Url) };  // e.g. a Google Images result: the picture itself, not Google's page
        o ??= new AddOptions();
        var site = job.Site == true || (Nexonic.Core.SiteVideo.IsSupported(job.Url) && job.Stream != true);
        var stream = !site && (job.Stream == true || HLS.IsPlaylistUrl(job.Url));
        var name = job.FileName is { Length: > 0 } n ? FileNames.Sanitize(n) : FileNames.FromUrl(job.Url);
        if (stream) name = HLS.PendingFileName(name);
        if (site && string.IsNullOrEmpty(job.FileName)) name = $"Video from {new Uri(job.Url).Host}";
        // A link the user asked to refresh: keep what is downloaded and continue from the new address.
        if (Rows.FirstOrDefault(r => r.Record.AwaitingRefresh == true && r.Record.FileName == name) is { } waiting)
        {
            var w = waiting.Record;
            w.Url = job.Url;
            w.Cookies = job.Cookies ?? w.Cookies;
            w.Referrer = job.Referrer ?? w.Referrer;
            w.AwaitingRefresh = null;
            w.Status = DownloadStatus.Queued;
            w.Error = null;
            waiting.Refresh();
            Save();
            Schedule();
            return w.Id;
        }
        // Settings for this website (Options → Sites): its folder unless another one was chosen, its login, user agent and limit.
        var profile = HostProfiles.Match(Settings.HostProfiles, job.Url);
        var chosenFolder = job.Folder ?? o.Folder;
        if (profile?.Folder is { Length: > 0 } siteFolder && (chosenFolder == null || chosenFolder == Settings.Folder)) chosenFolder = siteFolder;
        var limit = job.SpeedLimitKBps ?? (o.SpeedLimitKBps > 0 ? o.SpeedLimitKBps : profile?.SpeedLimitKBps ?? 0);
        var start = job.Start ?? (profile?.StartAtOnce == false ? false : o.Start);
        var rec = new DownloadRecord
        {
            Torrent = SmartDownload.Detect(job.Url) == DownloadEngine.Torrent ? new TorrentMetadata { MetadataPending = true } : null,
            Url = job.Url, Referrer = job.Referrer, Cookies = job.Cookies, UserAgent = profile?.UserAgent is { Length: > 0 } ua ? ua : job.UserAgent,
            Folder = chosenFolder ?? Settings.Folder,
            FileName = name,
            QueueId = job.Queue is { Length: > 0 } qn ? QueueIdNamed(qn)
                : o.QueueId == DownloadQueue.MainId && profile?.QueueName is { Length: > 0 } pq ? QueueIdNamed(pq) : o.QueueId,
            Username = job.Username ?? NullIfEmpty(o.Username) ?? NullIfEmpty(profile?.Username ?? ""),
            Password = job.Password ?? NullIfEmpty(o.Password) ?? (job.Username == null && NullIfEmpty(o.Username) == null ? NullIfEmpty(profile?.Password ?? "") : null),
            Checksum = job.Checksum ?? NullIfEmpty(o.Checksum),
            SpeedLimitKBps = limit > 0 ? limit : null,
            Status = start ? DownloadStatus.Queued : DownloadStatus.Paused,
            IsStream = stream ? true : null,
            SiteVideo = site ? true : null,
            AudioOnly = site && (job.AudioOnly ?? o.AudioOnly) ? true : null,
            InfoJson = site ? job.InfoJson : null,
            MaxHeight = site && !(job.AudioOnly ?? o.AudioOnly) ? job.MaxHeight : null,
        };
        Rows.Insert(0, new Row(rec, QueueName));
        Schedule();
        Save();
        return rec.Id;
    }

    /// <summary>Explicit, previewed torrent entry point. Browser/CLI/local API validators remain HTTP-only.</summary>
    public Guid AddTorrent(string source, TorrentMetadata metadata, AddOptions options)
    {
        if (TorrentRuntime.Unavailable(Settings) is { } reason) throw new InvalidOperationException(reason);
        if (SmartDownload.Detect(source) != DownloadEngine.Torrent || metadata.Files.Count == 0 || string.IsNullOrEmpty(metadata.Metainfo))
            throw new ArgumentException("Preview a local torrent or magnet before adding it.");
        var rec = new DownloadRecord { Url = source, FileName = metadata.Name, Size = metadata.SelectedSize,
            Folder = options.Folder ?? Settings.Folder, QueueId = options.QueueId, Torrent = metadata,
            Status = options.Start ? DownloadStatus.Queued : DownloadStatus.Paused,
            SpeedLimitKBps = options.SpeedLimitKBps > 0 ? options.SpeedLimitKBps : null };
        Rows.Insert(0, new Row(rec, QueueName));
        Save();
        Schedule();
        return rec.Id;
    }

    public void SetTorrentPriorities(Row row, IReadOnlyList<int> priorities)
    {
        var torrent = row.Record.Torrent ?? throw new ArgumentException("This is not a torrent.");
        // Validate a copy before altering a running task or its previous selection.
        var candidate = new TorrentMetadata { Files = torrent.Files, FilePriorities = priorities.ToList() };
        var selectedSize = candidate.SelectedSize;
        var changed = !torrent.Priorities().SequenceEqual(candidate.Priorities());
        Pause(row);
        torrent.FilePriorities = candidate.FilePriorities;
        if (changed)
        {
            torrent.PayloadComplete = false; torrent.CompletionNotified = false; torrent.Sha256.Clear();
            torrent.Received = 0; // The new selection must be verified again.
            row.Record.SavedPath = null; row.Record.Finished = null;
        }
        row.Record.Size = selectedSize;
        row.Record.Status = DownloadStatus.Paused;
        row.Refresh();
        Save();
    }

    /// <summary>Saves a torrent's limits, connections, seeding, file priorities and trackers. A running torrent takes them
    /// right away; a stopped one uses them when it starts.</summary>
    public void ApplyTorrentSettings(Row row, TorrentMetadata settings, int limitKBps, IReadOnlyList<int> priorities, List<string> trackers)
    {
        var torrent = row.Record.Torrent ?? throw new ArgumentException("This is not a torrent.");
        var candidate = new TorrentMetadata { Files = torrent.Files, FilePriorities = priorities.ToList() };
        var selectedSize = candidate.SelectedSize;
        var changed = !torrent.Priorities().SequenceEqual(candidate.Priorities());
        var trackersChanged = !trackers.SequenceEqual(torrent.Trackers);
        torrent.Seed = settings.Seed; torrent.Dht = settings.Dht; torrent.RatioLimit = settings.RatioLimit; torrent.SeedSeconds = settings.SeedSeconds;
        torrent.UploadLimitKBps = settings.UploadLimitKBps; torrent.MaxConnections = settings.MaxConnections; torrent.MaxUploads = settings.MaxUploads;
        torrent.Sequential = settings.Sequential; torrent.Trackers = trackers;
        row.Record.SpeedLimitKBps = limitKBps;
        if (transfers.TryGetValue(row.Record.Id, out var t) && t is TorrentTransfer running
            && running.Apply(torrent, limitKBps, changed ? candidate.Priorities() : null, trackersChanged ? trackers : null))
        {
            torrent.FilePriorities = candidate.FilePriorities;
            if (changed) { torrent.CompletionNotified = false; row.Record.Size = selectedSize; }
            row.Refresh(); Save();
            return;
        }
        if (changed) SetTorrentPriorities(row, priorities);
        else { row.Refresh(); Save(); }
    }

    /// <summary>Connected peers of a running torrent.</summary>
    public IReadOnlyList<TorrentPeer> TorrentPeers(Row row) =>
        transfers.TryGetValue(row.Record.Id, out var t) && t is TorrentTransfer running ? running.Peers() : Array.Empty<TorrentPeer>();
    /// <summary>Asks the trackers and DHT for more peers now.</summary>
    public void Reannounce(Row row) { if (transfers.TryGetValue(row.Record.Id, out var t) && t is TorrentTransfer running) running.Reannounce(); }
    public bool IsRunning(Row row) => transfers.ContainsKey(row.Record.Id);

    static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>
    /// Downloads handed over by the browser extension and the ndm command. A link clicked in the browser opens the
    /// download window (unless turned off); the same link already downloading is never added twice.
    /// </summary>
    /// <summary>A .torrent file or magnet link opened from outside the app, to show in the Add Torrent window.</summary>
    public event Action<string>? TorrentRequested;
    /// <summary>Shows the Add Torrent window for each torrent opened from the file manager or a browser.</summary>
    public void CollectTorrents()
    {
        if (TorrentRequested == null) return;
        foreach (var source in TorrentInbox.Collect()) TorrentRequested(source);
    }

    public void CollectInbox()
    {
        CollectTorrents();
        var browser = false;
        foreach (var j in new Inbox().Collect())
        {
            if (j.FromBrowser == true) browser = true;
            if (Rows.FirstOrDefault(r => r.Record.Url == j.Url && (r.Record.IsActive || r.Record.Status == DownloadStatus.Queued)) is { } dup)
            {
                if (Settings.ShowProgressWindow && j.FromBrowser == true) ProgressRequested?.Invoke(dup);
                continue;
            }
            if (j.FromBrowser == true && Settings.ShowDownloadDialog && DownloadDialogRequested != null)
            {
                DownloadDialogRequested(j);
                continue;
            }
            var id = Add(j);
            if (id is Guid g && j.FromBrowser == true && j.Start != false && Settings.ShowProgressWindow
                && Rows.FirstOrDefault(r => r.Record.Id == g) is { } row)
                ProgressRequested?.Invoke(row);
        }
        if (browser && Settings.BringToFrontOnBrowserDownload) ShowRequested?.Invoke();
    }

    // ---- control ----

    public void Pause(Row row)
    {
        if (transfers.Remove(row.Record.Id, out var t)) t.Pause();
        else if (row.Record.Status == DownloadStatus.Queued) row.Record.Status = DownloadStatus.Paused;
        row.Refresh();
        Schedule();
    }

    public void Resume(Row row)
    {
        if (row.Record.Status is DownloadStatus.Paused or DownloadStatus.Failed)
        {
            row.Record.Status = DownloadStatus.Queued;
            row.Record.Error = null;
        }
        if (Queues.FirstOrDefault(q => q.Id == row.Record.Queue) is { Running: false } queue) queue.Running = true;
        row.Refresh();
        Schedule();
    }

    /// <summary>Stops the download and deletes what was downloaded, but keeps it in the list as Canceled (Resume starts it
    /// again from the beginning). Torrents are removed from the list as before; their files stay on disk.</summary>
    public void Cancel(Row row)
    {
        var r = row.Record;
        if (r.Torrent != null) { Remove(row); return; }
        if (transfers.Remove(r.Id, out var t)) t.Discard();
        if (r.Status == DownloadStatus.Finished) return;
        Transfer.DeleteTemporaryFiles(r);
        r.Status = DownloadStatus.Failed;
        r.Error = DownloadRecord.CanceledMessage;
        r.Segments = new();
        r.VideoParts = null;
        r.VideoStep = null;
        r.StreamDone = null;
        r.StreamBytes = null;
        row.Refresh();
        Save();
        Schedule();
    }

    public void Remove(Row row)
    {
        if (transfers.Remove(row.Record.Id, out var t)) t.Discard();
        Transfer.DeleteTemporaryFiles(row.Record);
        try { if (row.Record.InfoJson is { } info) File.Delete(info); } catch { }
        Rows.Remove(row);
        Save();
        Schedule();
    }

    public void PauseAll() { foreach (var r in Rows.ToList()) if (r.Record.IsActive || r.Record.Status == DownloadStatus.Queued) Pause(r); }
    public void ResumeAll() { foreach (var r in Rows.ToList()) if (r.Record.Status is DownloadStatus.Paused or DownloadStatus.Failed) Resume(r); }
    public void ClearFinished() { foreach (var r in Rows.Where(r => r.Record.Status == DownloadStatus.Finished).ToList()) Remove(r); }

    /// <summary>Continues from a new address (or waits for one from the browser), keeping what is downloaded.</summary>
    public void Refresh(Row row, string? link)
    {
        if (transfers.Remove(row.Record.Id, out var t)) t.Pause();
        var r = row.Record;
        if (link != null && new DownloadJob(link).IsValid)
        {
            r.Url = link.Trim();
            r.AwaitingRefresh = null;
            r.Status = DownloadStatus.Queued;
            r.Error = null;
        }
        else
        {
            r.AwaitingRefresh = true;
            r.Status = DownloadStatus.Paused;
            r.Error = "Waiting for a fresh link from the browser";
        }
        row.Refresh();
        Save();
        Schedule();
    }

    public void SetDetails(Row row, string username, string password, string checksum, int limitKBps, string queueId)
    {
        var r = row.Record;
        r.Username = NullIfEmpty(username);
        r.Password = NullIfEmpty(password);
        r.Checksum = NullIfEmpty(checksum);
        r.SpeedLimitKBps = limitKBps > 0 ? limitKBps : null;
        r.QueueId = queueId;
        if (transfers.TryGetValue(r.Id, out var t)) t.OwnLimiter.SetLimit(Math.Max(0, limitKBps) * 1024);
        row.Refresh();
        Save();
        Schedule();
    }

    public string Verify(Row row, string expected)
    {
        if (row.Record.SavedPath is not { } path || !File.Exists(path)) return "The file is not downloaded yet";
        if (Nexonic.Core.Checksum.Parse(expected) == null) return "Paste an MD5, SHA-1, SHA-256 or SHA-512 value";
        var result = Nexonic.Core.Checksum.Verify(expected, path);
        row.Record.Checksum = expected;
        row.Record.ChecksumResult = result;
        row.Refresh();
        Save();
        return result;
    }

    // ---- queues and scheduler ----

    public string QueueName(string id) => Queues.FirstOrDefault(q => q.Id == id) is { } q ? (q.IsMain ? L.T(q.Name) : q.Name) : L.T("Main queue");

    string QueueIdNamed(string name)
    {
        if (Queues.FirstOrDefault(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase)) is { } found) return found.Id;
        var q = new DownloadQueue { Name = name, Running = true };
        Queues.Add(q);
        SaveQueues();
        return q.Id;
    }

    public DownloadQueue NewQueue()
    {
        var n = Queues.Count + 1;
        while (Queues.Any(q => q.Name == $"Queue {n}")) n++;
        var queue = new DownloadQueue { Name = $"Queue {n}" };
        Queues.Add(queue);
        SaveQueues();
        return queue;
    }

    public void DeleteQueue(DownloadQueue q)
    {
        if (q.IsMain) return;
        foreach (var r in Rows.Where(r => r.Record.QueueId == q.Id)) { r.Record.QueueId = null; r.Refresh(); }
        Queues.Remove(q);
        SaveQueues();
        Save();
        Schedule();
    }

    public void StartQueue(string id)
    {
        if (Queues.FirstOrDefault(q => q.Id == id) is not { } queue) return;
        queue.Running = true;
        foreach (var row in Rows.Where(r => r.Record.Queue == id && r.Record.AwaitingRefresh != true &&
                                            r.Record.Status is DownloadStatus.Paused or DownloadStatus.Failed))
        {
            row.Record.Status = DownloadStatus.Queued;
            row.Record.Error = null;
            row.Refresh();
        }
        SaveQueues();
        Save();
        Schedule();
    }

    public void StopQueue(string id)
    {
        if (Queues.FirstOrDefault(q => q.Id == id) is not { } queue) return;
        queue.Running = false;
        foreach (var row in Rows.Where(r => r.Record.Queue == id && r.Record.IsActive).ToList())
        {
            if (transfers.Remove(row.Record.Id, out var t)) t.Pause();
            row.Record.Status = DownloadStatus.Queued; // waits for the queue to start again
            row.Refresh();
        }
        SaveQueues();
        Save();
    }

    public void SaveQueues() => Write(AppPaths.Queues, Queues.ToList());

    void RunTimetable()
    {
        var now = DateTime.Now;
        var minute = QueueClock.HhMm(now);
        if (minute == lastMinute) return;
        lastMinute = minute;
        foreach (var q in Queues.ToList())
        {
            if (QueueClock.ShouldStart(q, now))
            {
                StartQueue(q.Id);
                q.LastStartDay = QueueClock.DayKey(now);
                SaveQueues();
            }
            else if (QueueClock.ShouldStop(q, now)) StopQueue(q.Id);
        }
    }

    void CheckQueuesDone()
    {
        foreach (var q in Queues.Where(q => q.Running).ToList())
        {
            var busy = Rows.Any(r => r.Record.Queue == q.Id && (r.Record.IsActive || r.Record.Status == DownloadStatus.Queued));
            if (busy) { queuesWithWork.Add(q.Id); continue; }
            if (!queuesWithWork.Remove(q.Id)) continue;
            if (!q.IsMain) { q.Running = false; SaveQueues(); }
            AfterQueue(q);
        }
    }

    void AfterQueue(DownloadQueue q)
    {
        switch (q.AfterDone)
        {
            case Nexonic.Core.AfterQueue.Quit:
                Save();
                QuitRequested?.Invoke();
                break;
            case Nexonic.Core.AfterQueue.Sleep:
                Notify?.Invoke("Queue finished", $"{q.Name} is done. The computer will sleep now.");
                System.Windows.Forms.Application.SetSuspendState(System.Windows.Forms.PowerState.Suspend, false, false);
                break;
            case Nexonic.Core.AfterQueue.ShutDown:
                Save();
                Notify?.Invoke("Queue finished", $"{q.Name} is done. Windows shuts down in one minute (shutdown /a cancels).");
                try { Process.Start(new ProcessStartInfo("shutdown", "/s /t 60") { CreateNoWindow = true, UseShellExecute = false }); } catch { }
                break;
        }
    }

    void Schedule()
    {
        foreach (var q in Queues.Where(q => q.Running).ToList())
        {
            var limit = q.IsMain ? Settings.MaxActive : q.MaxActive;
            var slots = Math.Max(1, limit) - transfers.Values.Count(t => t.Record.Queue == q.Id && t.Record.Status != DownloadStatus.Seeding);
            foreach (var row in Rows.Reverse().Where(r => r.Record.Queue == q.Id && r.Record.Status == DownloadStatus.Queued).ToList())
            {
                if (slots-- <= 0) break;
                var profile = HostProfiles.Match(Settings.HostProfiles, row.Record.Url);
                var proxy = profile?.Proxy ?? Settings.Proxy;
                var connections = Math.Clamp(profile?.MaxConnections ?? Settings.Connections, 1, 32);
                var context = new DownloadContext(connections, limiter, Settings.SortIntoCategoryFolders, proxy,
                    TransferOptions.From(Network.Options, profile), Settings.TorrentsEnabled, Settings.SpeedLimitKBps,
                    Settings.TorrentPortForwarding, Settings.TorrentEncryption);
                var t = new DownloadProviders().Create(row.Record, context);
                transfers[row.Record.Id] = t;
                row.Record.Status = DownloadStatus.Probing;
                t.Changed += rec => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Changed(row, rec, t));
                _ = t.StartAsync();
            }
        }
    }

    void Changed(Row row, DownloadRecord rec, IDownloader t)
    {
        if (!transfers.TryGetValue(rec.Id, out var current) || current != t) return; // already stopped by the app
        row.Refresh();
        if (rec.Torrent is { PayloadComplete: true, CompletionNotified: false } torrent)
        {
            torrent.CompletionNotified = true;
            AfterDownload(rec);
            Save();
            if (rec.Status == DownloadStatus.Seeding) Schedule();
        }
        if (rec.Status is not (DownloadStatus.Finished or DownloadStatus.Failed or DownloadStatus.Paused)) return;
        transfers.Remove(rec.Id);
        if (rec.Status == DownloadStatus.Finished && rec.Torrent == null) AfterDownload(rec);
        if (rec.Status == DownloadStatus.Failed) Notify?.Invoke("Download failed", $"{rec.FileName}: {rec.Error}");
        Save();
        Schedule();
    }

    // ---- after a download ----

    void AfterDownload(DownloadRecord rec)
    {
        if (rec.SavedPath is not { } path) return;
        if (Settings.MarkDownloads)
        {
            // Mark of the web: Windows checks the file (SmartScreen, Office protected view) as it does for browser downloads.
            try { File.WriteAllText(path + ":Zone.Identifier", $"[ZoneTransfer]\r\nZoneId=3\r\nHostUrl={rec.Url}\r\n"); } catch { }
        }
        if (Settings.PlaySound) System.Media.SystemSounds.Asterisk.Play();
        if (Settings.ScanEnabled) Scan(path);
        Finished?.Invoke(rec);
    }

    void Scan(string path)
    {
        var words = CommandLineTool.Words(Environment.ExpandEnvironmentVariables(Settings.ScanCommand));
        if (words.Count == 0) return;
        var name = Path.GetFileName(path);
        Task.Run(() =>
        {
            bool ok;
            try
            {
                var psi = new ProcessStartInfo(words[0]) { CreateNoWindow = true, UseShellExecute = false };
                foreach (var w in words.Skip(1)) psi.ArgumentList.Add(w);
                psi.ArgumentList.Add(path);
                using var p = Process.Start(psi)!;
                p.WaitForExit();
                ok = p.ExitCode == 0;
            }
            catch { ok = false; }
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                Notify?.Invoke(ok ? "Virus scan passed" : "Virus scan warning", ok ? name : $"{name}: the scanner reported a problem or could not run"));
        });
    }

    // ---- clipboard and timer ----

    void WatchClipboard()
    {
        var seq = GetClipboardSequenceNumber();
        if (seq == clipboardSequence) return;
        clipboardSequence = seq;
        if (!Settings.MonitorClipboard) return;
        string text;
        try { text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : ""; } catch { return; }
        if (FileTypes.DownloadLink(text, FileTypes.Parse(Settings.ClipboardTypes)) is { } u && Rows.All(r => r.Record.Url != u.AbsoluteUri && r.Record.Url != text.Trim()))
            ClipboardLink?.Invoke(u.AbsoluteUri);
    }

    void Tick()
    {
        foreach (var r in Rows) if (r.Record.IsActive) r.Tick();
        WatchClipboard();
        if (++ticks % 2 == 0)
        {
            CollectInbox();
            RunTimetable();
            CheckQueuesDone();
        }
        if (ticks % 4 == 0) Save();
    }

    // ---- backup ----

    /// <summary>Settings, queues and the list as a backup file (passwords and cookies are left out).</summary>
    public string ExportBackup(string appVersion) => Backup.Export(Settings, Queues, Rows.Select(r => r.Record), appVersion);

    /// <summary>Adds the backup's queues and downloads that are not here yet; optionally takes its settings. Returns how many downloads were added.</summary>
    public int ImportBackup(string json, bool settingsToo)
    {
        var file = Backup.Parse(json);
        foreach (var q in file.Queues ?? new())
            if (!Queues.Any(x => x.Id == q.Id) && !q.IsMain) { q.Running = false; Queues.Add(q); }
        var added = Backup.NewDownloads(file, Rows.Select(r => r.Record));
        foreach (var r in added.AsEnumerable().Reverse())
        {
            if (r.QueueId != null && Queues.All(q => q.Id != r.QueueId)) r.QueueId = null;
            Rows.Insert(0, new Row(r, QueueName));
        }
        if (settingsToo && file.Settings is { } s)
        {
            // Passwords are not in backups: keep the ones saved here.
            s.Proxy.Password = Settings.Proxy.Password;
            s.Automation = Settings.Automation;
            foreach (var p in s.HostProfiles)
                if (Settings.HostProfiles.FirstOrDefault(x => HostProfiles.Normalize(x.Host) == HostProfiles.Normalize(p.Host)) is { } mine)
                {
                    p.Password = mine.Password;
                    if (p.Proxy != null && mine.Proxy != null) p.Proxy.Password = mine.Proxy.Password;
                }
            ApplySettings(s);
        }
        SaveQueues();
        Save();
        Schedule();
        return added.Count;
    }

    public void Save() => Write(AppPaths.Downloads, Rows.Select(r => r.Record).ToList());

    /// <summary>Saves atomically, with cookies and passwords encrypted for this Windows user (see Secrets).</summary>
    static void Write<T>(string path, T value)
    {
        try { SafeFile.Write(path, Secrets.SerializeSealed(value)); }
        catch { }
    }

    static T? Load<T>(string path) => SafeFile.Load(path, Secrets.DeserializeOpened<T>);
}

/// <summary>One row in the table; raises change notifications for the bound columns.</summary>
public sealed class Row : System.ComponentModel.INotifyPropertyChanged
{
    public DownloadRecord Record { get; }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    readonly Func<string, string> queueName;
    readonly Queue<long> recent = new();   // received bytes at each of the last ticks
    public double SpeedBytes { get; private set; }

    public Row(DownloadRecord r, Func<string, string> queueName)
    {
        Record = r;
        this.queueName = queueName;
    }

    public string Name => Record.FileName;
    public string Size => Record.Size is long s ? Bytes(s) : "–";
    public double Percent => Record.Progress * 100;
    public string Speed => Record.IsActive && SpeedBytes > 0 ? Bytes((long)SpeedBytes) + "/s" : "";
    public string TimeLeft => Record.IsActive ? TimeLeftText.Of(Record.Size, Record.Received, SpeedBytes) : "";
    /// <summary>Video sites and streams take a few seconds to look up the best quality before the first byte arrives.</summary>
    public bool PreparingVideo => Record.IsActive && (Record.SiteVideo == true || Record.IsStream == true) && Record.Received == 0;
    public string Status => PreparingVideo ? L.T("Preparing video…") : Record.Status switch
    {
        DownloadStatus.Queued => L.T("Queued"),
        DownloadStatus.Probing => L.T("Connecting…"),
        DownloadStatus.Downloading => $"{Percent:0}%",
        DownloadStatus.Seeding => "Seeding",
        DownloadStatus.Paused => L.T(Record.AwaitingRefresh == true ? "Waiting for link" : "Stopped"),
        DownloadStatus.Finished => L.T(Record.ChecksumResult?.Contains("not") == true ? "Checksum failed" : "Complete"),
        _ => L.T(Record.IsCanceled ? "Canceled" : "Error"),
    };
    public string Queue => queueName(Record.Queue);
    public System.Windows.Media.ImageSource? Icon => FileIcon.For(Record.FileName);
    public System.Windows.Media.Brush StatusBrush => Record.Status switch
    {
        DownloadStatus.Downloading or DownloadStatus.Probing or DownloadStatus.Seeding => Theme.Accent,
        DownloadStatus.Queued => Theme.Waiting,
        DownloadStatus.Paused => Theme.Idle,
        DownloadStatus.Finished => Record.ChecksumResult?.Contains("not") == true ? Theme.Bad : Theme.Good,
        _ => Record.IsCanceled ? Theme.Idle : Theme.Bad,
    };
    public string Added => Record.Added.ToString("d MMM yyyy, HH:mm");
    public string Category => Record.Category.ToString();

    public void Tick()
    {
        // Averaged over the last 6 seconds: video sites deliver in bursts, so a single tick is often 0.
        recent.Enqueue(Record.Received);
        while (recent.Count > 13) recent.Dequeue();
        SpeedBytes = recent.Count > 1 ? Math.Max(0, (recent.Last() - recent.Peek()) / ((recent.Count - 1) * 0.5)) : 0;
        Refresh();
    }

    public void Refresh() => PropertyChanged?.Invoke(this, new(""));

    public static string Bytes(long n)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = n;
        var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }
}

public static class Theme
{
    static System.Windows.Media.SolidColorBrush B(byte r, byte g, byte b)
    {
        var x = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        x.Freeze();
        return x;
    }
    public static readonly System.Windows.Media.Brush Accent = B(0x1F, 0x6F, 0xEB);
    public static readonly System.Windows.Media.Brush Waiting = B(0xD9, 0x82, 0x1E);
    public static readonly System.Windows.Media.Brush Idle = B(0x8B, 0x94, 0x9E);
    public static readonly System.Windows.Media.Brush Good = B(0x2D, 0xA4, 0x4E);
    public static readonly System.Windows.Media.Brush Bad = B(0xCF, 0x22, 0x2E);
}

/// <summary>The system's own small icon for each file type, cached by extension.</summary>
public static class FileIcon
{
    static readonly Dictionary<string, System.Windows.Media.ImageSource?> cache = new();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);

    public static System.Windows.Media.ImageSource? For(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (cache.TryGetValue(ext, out var found)) return found;
        System.Windows.Media.ImageSource? image = null;
        try
        {
            var info = new SHFILEINFO();
            const uint Icon = 0x100, Small = 0x1, UseAttributes = 0x10, Normal = 0x80;
            SHGetFileInfo("file" + ext, Normal, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), Icon | Small | UseAttributes);
            if (info.hIcon != IntPtr.Zero)
            {
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(info.hIcon, System.Windows.Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                image = src;
                DestroyIcon(info.hIcon);
            }
        }
        catch { }
        cache[ext] = image;
        return image;
    }
}
