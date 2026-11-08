namespace Nexonic.Core;

public enum DownloadStatus { Queued, Probing, Downloading, Paused, Finished, Failed, Seeding }

/// <summary>One byte range. End is inclusive; -1 means "until the server stops" (size unknown).</summary>
public sealed class Segment
{
    public int Id { get; set; }
    public long Start { get; set; }
    public long End { get; set; }
    public long Written { get; set; }

    public long? Length => End < 0 ? null : End - Start + 1;
    public long Remaining => End < 0 ? long.MaxValue : Math.Max(0, End - Start + 1 - Written);
    public bool IsComplete => End >= 0 && Written >= End - Start + 1;
}

/// <summary>One piece of a video from a video site (the picture or the sound), shown in the progress window.</summary>
public sealed class VideoPart
{
    public string Name { get; set; } = "";
    public long Done { get; set; }
    public long? Total { get; set; }
}

public sealed class DownloadRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Url { get; set; } = "";
    public string? Referrer { get; set; }
    public string? Cookies { get; set; }
    public string? UserAgent { get; set; }
    public string FileName { get; set; } = "";
    public string Folder { get; set; } = "";
    public long? Size { get; set; }
    public bool AcceptsRanges { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public List<Segment> Segments { get; set; } = new();
    public DateTime Added { get; set; } = DateTime.Now;
    public DateTime? Finished { get; set; }
    public string? Error { get; set; }
    public string? SavedPath { get; set; }
    // Added in 0.2.0
    public string? QueueId { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Checksum { get; set; }
    public string? ChecksumResult { get; set; }
    public int? SpeedLimitKBps { get; set; }
    public bool? AwaitingRefresh { get; set; }
    // Added in 0.3.0: streamed video
    public bool? IsStream { get; set; }
    public int? StreamDone { get; set; }
    public int? StreamTotal { get; set; }
    public long? StreamBytes { get; set; }
    // Added in 0.4.0: videos from YouTube and other sites
    public bool? SiteVideo { get; set; }
    public bool? AudioOnly { get; set; }
    // Added in 0.5.5: video details looked up while the download window was open (path of a yt-dlp info file)
    public string? InfoJson { get; set; }
    // Added in 0.6.2: the file's ETag or Last-Modified when the download started, so a changed file is never resumed into
    public string? Validator { get; set; }
    // Added in 0.19.0: the file the download is written into ("name.nxpart" in the save folder), renamed when complete
    public string? WorkFile { get; set; }
    // Added in 0.6.3: the highest picture quality chosen for a video (null = the best on offer)
    public int? MaxHeight { get; set; }
    // Added in 0.6.0: the picture and sound pieces of a video, and the step after downloading (joining them)
    public List<VideoPart>? VideoParts { get; set; }
    public string? VideoStep { get; set; }

    public TorrentMetadata? Torrent { get; set; }

    /// <summary>The error text of a download the user canceled: it stays in the list, and Resume starts it again.</summary>
    public const string CanceledMessage = "Canceled";
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCanceled => Status == DownloadStatus.Failed && Error == CanceledMessage;
    public long Received { get { if (Torrent != null) return Torrent.Received; if (IsStream == true || SiteVideo == true) return StreamBytes ?? 0; lock (Segments) return Segments.Sum(s => s.Written); } }
    public double Progress => IsStream == true
        ? (StreamTotal is > 0 ? Math.Min(1, (double)(StreamDone ?? 0) / StreamTotal.Value) : 0)
        : Size is > 0 ? Math.Min(1, (double)Received / Size.Value) : 0;
    public FileCategory Category => FileCategories.Of(FileName);
    public bool IsActive => Status is DownloadStatus.Probing or DownloadStatus.Downloading or DownloadStatus.Seeding;
    public string Queue => QueueId ?? DownloadQueue.MainId;
}

public enum AfterQueue { Nothing, Quit, Sleep, ShutDown }

/// <summary>A list of downloads with its own limit and timetable. The main queue starts new downloads straight away.</summary>
public sealed class DownloadQueue
{
    public const string MainId = "main";
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public bool Running { get; set; }
    public int MaxActive { get; set; } = 3;
    public bool StartEnabled { get; set; }
    public string StartTime { get; set; } = "02:00";
    public bool StopEnabled { get; set; }
    public string StopTime { get; set; } = "08:00";
    public List<int> Days { get; set; } = new() { 1, 2, 3, 4, 5, 6, 7 }; // 1 = Monday … 7 = Sunday
    public AfterQueue AfterDone { get; set; }
    public string? LastStartDay { get; set; }

    public static DownloadQueue Main() => new() { Id = MainId, Name = "Main queue", Running = true };
    public bool IsMain => Id == MainId;

    public static string AfterTitle(AfterQueue a) => a switch
    {
        AfterQueue.Quit => "Quit the app",
        AfterQueue.Sleep => "Put the computer to sleep",
        AfterQueue.ShutDown => "Shut down the computer",
        _ => "Do nothing",
    };
}

/// <summary>Decides when a queue's timetable starts or stops it.</summary>
public static class QueueClock
{
    public static int Weekday(DateTime t) => t.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)t.DayOfWeek;
    public static string HhMm(DateTime t) => t.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    public static string DayKey(DateTime t) => t.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static bool ShouldStart(DownloadQueue q, DateTime t) =>
        q.StartEnabled && !q.Running && q.Days.Contains(Weekday(t)) && q.LastStartDay != DayKey(t) && HhMm(t) == q.StartTime;

    public static bool ShouldStop(DownloadQueue q, DateTime t) => q.StopEnabled && q.Running && HhMm(t) == q.StopTime;
}

public enum ProxyMode { System, None, Http, Socks }

public sealed class ProxySettings
{
    public ProxyMode Mode { get; set; } = ProxyMode.System;
    public string Host { get; set; } = "";
    public int Port { get; set; } = 8080;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    public static string Title(ProxyMode m) => m switch
    {
        ProxyMode.None => "No proxy",
        ProxyMode.Http => "HTTP proxy",
        ProxyMode.Socks => "SOCKS5 proxy",
        _ => "Use the system settings",
    };
}

/// <summary>File types that count as downloads when a link is copied.</summary>
public static class FileTypes
{
    public const string DefaultList =
        "zip rar 7z gz tgz bz2 xz tar iso dmg pkg exe msi apk deb rpm mp3 m4a aac flac wav ogg mp4 mkv mov avi webm m4v wmv flv pdf epub doc docx xls xlsx ppt pptx";

    public static HashSet<string> Parse(string? text) =>
        (text ?? "").ToLowerInvariant().Split(new[] { ' ', ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.TrimStart('.')).ToHashSet();

    /// <summary>A copied text counts when it is one web link to a file of a listed type.</summary>
    public static Uri? DownloadLink(string? text, HashSet<string> types)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0 || t.Any(char.IsWhiteSpace) || !new DownloadJob(t).IsValid) return null;
        var u = new Uri(t);
        var ext = Path.GetExtension(u.AbsolutePath).TrimStart('.').ToLowerInvariant();
        return types.Contains(ext) ? u : null;
    }
}

public sealed class AppSettings
{
    /// <summary>Bundled native libtorrent engine; off for existing and new installations.</summary>
    public bool TorrentsEnabled { get; set; }
    /// <summary>Ask the router to forward the torrent port (UPnP and NAT-PMP); off unless turned on.</summary>
    public bool TorrentPortForwarding { get; set; }
    /// <summary>Torrent peer encryption: prefer, require or off.</summary>
    public string TorrentEncryption { get; set; } = "prefer";
    public int Connections { get; set; } = 8;
    public int MaxActive { get; set; } = 3;
    public int SpeedLimitKBps { get; set; }
    public string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    /// <summary>Each type in its own folder (Video, Music, Documents…), like IDM. On by default since 0.6.6 (saved under a
    /// new name, so earlier installs get it too).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("CategoryFolders")]
    public bool SortIntoCategoryFolders { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public bool MonitorClipboard { get; set; } = true;
    public string ClipboardTypes { get; set; } = FileTypes.DefaultList;
    public ProxySettings Proxy { get; set; } = new();
    /// <summary>Advanced connection settings.</summary>
    public NetworkOptions Network { get; set; } = new();
    /// <summary>Settings for downloads from particular websites.</summary>
    public List<HostProfile> HostProfiles { get; set; } = new();
    /// <summary>Optional local automation API (off by default).</summary>
    public AutomationSettings Automation { get; set; } = new();
    /// <summary>Language code ("ur", "de"…); empty = the system's language.</summary>
    public string Language { get; set; } = "";
    public string Theme { get; set; } = "Light";
    public bool ShowCompleteDialog { get; set; } = true;
    public bool PlaySound { get; set; } = true;
    public bool MarkDownloads { get; set; } = true;
    public bool ScanEnabled { get; set; }
    // Added in 0.5.0: download and progress windows
    public bool ShowDownloadDialog { get; set; } = true;
    public bool ShowProgressWindow { get; set; } = true;
    public bool BringToFrontOnBrowserDownload { get; set; }
    // Added in 0.6.0: in-app updates
    public bool CheckForUpdates { get; set; } = true;
    public string SkippedVersion { get; set; } = "";
    // Added in 0.14.0: IDM-style toolbar and menus on Windows and Linux (View menu)
    public bool ShowToolbar { get; set; } = true;
    public bool ToolbarText { get; set; } = true;
    /// <summary>Unzip finished .zip downloads into a folder next to them (off unless turned on).</summary>
    public bool ExtractZips { get; set; }
    /// <summary>Save subtitles (the computer's language and English) inside downloaded videos (added in 0.16.0, off unless turned on).</summary>
    public bool VideoSubtitles { get; set; }
    public string ScanCommand { get; set; } = "\"%ProgramFiles%\\Windows Defender\\MpCmdRun.exe\" -Scan -ScanType 3 -DisableRemediation -File";
}

public static class AppPaths
{
    public static string Support => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nexonic Download Manager");
    public static string Inbox => Path.Combine(Support, "Inbox");
    public static string Parts => Path.Combine(Support, "Parts");
    public static string Downloads => Path.Combine(Support, "downloads.json");
    public static string Settings => Path.Combine(Support, "settings.json");
    public static string Queues => Path.Combine(Support, "queues.json");
}

/// <summary>Time left as shown in the list and the progress window.</summary>
public static class TimeLeftText
{
    /// <summary>"1:05:09", "12:30" or "" (unknown); days are not dropped and absurd values never crash.</summary>
    public static string Of(long? size, long received, double bytesPerSecond)
    {
        if (size is not long s || bytesPerSecond <= 0 || s <= 0) return "";
        var seconds = Math.Max(0, (s - received) / bytesPerSecond);
        if (double.IsNaN(seconds) || seconds > 99 * 24 * 3600) return seconds > 0 ? "more than 99 days" : "";
        var t = (long)Math.Ceiling(seconds);
        var h = t / 3600;
        return h > 0 ? $"{h}:{t / 60 % 60:00}:{t % 60:00}" : $"{t / 60}:{t % 60:00}";
    }
}
