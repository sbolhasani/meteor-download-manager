using System.Net;
using System.Net.Http.Headers;

namespace Nexonic.Core;

/// <summary>What the app needs from any running download.</summary>
public interface IDownloader
{
    DownloadRecord Record { get; }
    SpeedLimiter OwnLimiter { get; }
    event Action<DownloadRecord>? Changed;
    Task StartAsync();
    void Pause();
    void Discard();
}

/// <summary>
/// Downloads streamed video (HLS): picks the best quality, fetches the pieces in parallel, resumes after a pause or
/// restart, joins them and, when ffmpeg is installed, turns the result into one .mp4 with picture and sound.
/// </summary>
public sealed class StreamTransfer : IDownloader
{
    public DownloadRecord Record { get; }
    public SpeedLimiter OwnLimiter { get; } = new();
    public event Action<DownloadRecord>? Changed;

    readonly SpeedLimiter limiter;
    readonly int connections;
    readonly bool sortIntoCategories;
    readonly HttpClient http;
    readonly object gate = new();
    CancellationTokenSource? cts;
    DateTime lastNotify = DateTime.MinValue;

    string PartsFolder => Path.Combine(AppPaths.Parts, Record.Id.ToString("N"));
    string Piece(string track, int i) => Path.Combine(PartsFolder, $"{track}-{i}.seg");

    public StreamTransfer(DownloadRecord record, int connections, SpeedLimiter limiter, bool sortIntoCategories = false, ProxySettings? proxy = null)
    {
        Record = record;
        this.limiter = limiter;
        this.connections = Math.Clamp(connections, 1, 8);
        this.sortIntoCategories = sortIntoCategories;
        OwnLimiter.SetLimit((record.SpeedLimitKBps ?? 0) * 1024);
        var handler = Network.Handler(proxy ?? new ProxySettings());
        // Redirects are followed by hand (Network.SendAsync) so cookies and the login never reach another host.
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public Task StartAsync()
    {
        cts = new CancellationTokenSource();
        return Task.Run(() => RunAsync(cts.Token));
    }

    public void Pause()
    {
        Record.Status = DownloadStatus.Paused;
        cts?.Cancel();
        Notify(true);
    }

    public void Discard()
    {
        cts?.Cancel();
        try { Directory.Delete(PartsFolder, true); } catch { }
    }

    void Notify(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && (now - lastNotify).TotalMilliseconds < 250) return;
        lastNotify = now;
        Changed?.Invoke(Record);
    }

    sealed class HttpFailure(int code) : Exception(code is 403 or 404 or 410
        ? $"The video link has expired (HTTP {code}). Play the video again and use Refresh Link"
        : $"The server answered HTTP {code}")
    {
        public int Code { get; } = code;
    }

    async Task<byte[]> FetchAsync(Uri url, CancellationToken ct)
    {
        var origin = new Uri(Record.Url);
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var res = await Network.SendAsync(http, url, u =>
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, u);
                    if (!string.IsNullOrEmpty(Record.Referrer)) req.Headers.TryAddWithoutValidation("Referer", Record.Referrer);
                    if (!string.IsNullOrEmpty(Record.UserAgent)) req.Headers.TryAddWithoutValidation("User-Agent", Record.UserAgent);
                    // Pieces often live on other hosts (CDNs): cookies and the login only go to the playlist's own server.
                    Network.AddPrivateHeaders(req, u, origin, Record.Cookies, Record.Username, Record.Password);
                    return req;
                }, HttpCompletionOption.ResponseContentRead, ct);
                if (!res.IsSuccessStatusCode) throw new HttpFailure((int)res.StatusCode);
                var data = await res.Content.ReadAsByteArrayAsync(ct);
                if (data.Length == 0) throw new IOException("An empty piece was received");
                return data;
            }
            catch (Exception) when (!ct.IsCancellationRequested && attempt < 6)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt - 1))), ct);
            }
        }
    }

    async Task<HLS.Playlist> PlaylistAsync(Uri url, CancellationToken ct) =>
        HLS.Parse(System.Text.Encoding.UTF8.GetString(await FetchAsync(url, ct)), url);

    async Task RunAsync(CancellationToken ct)
    {
        try
        {
            Record.Status = DownloadStatus.Probing;
            Record.Error = null;
            Record.IsStream = true;
            Notify(true);
            var tracks = new List<(string Name, HLS.Media Media)>();
            var top = await PlaylistAsync(new Uri(Record.Url), ct);
            if (!top.IsMaster) tracks.Add(("video", top.Media!));
            else
            {
                var best = HLS.Best(top.Variants)!;
                var video = await PlaylistAsync(best.Url, ct);
                tracks.Add(("video", video.Media ?? throw new HLS.Failure("This link is not a video playlist")));
                if (best.AudioGroup is { } g && top.Audio.TryGetValue(g, out var a) && (await PlaylistAsync(a, ct)).Media is { } sound)
                    tracks.Add(("audio", sound));
            }
            if (tracks.Any(t => t.Media.Encryption != "NONE")) throw new HLS.Failure("This video is copy-protected and can't be saved");
            Directory.CreateDirectory(PartsFolder);
            Record.StreamTotal = tracks.Sum(t => t.Media.Segments.Count + (t.Media.InitSegment == null ? 0 : 1));
            Record.StreamDone = 0;
            Record.StreamBytes = 0;
            Record.Status = DownloadStatus.Downloading;
            Notify(true);
            var joined = new List<(string Name, HLS.Media Media, string File)>();
            foreach (var (name, media) in tracks)
            {
                var pieces = media.Segments.ToList();
                if (media.InitSegment != null) pieces.Insert(0, media.InitSegment);
                await Parallel.ForEachAsync(Enumerable.Range(0, pieces.Count),
                    new ParallelOptions { MaxDegreeOfParallelism = connections, CancellationToken = ct },
                    async (i, token) => await GetAsync(pieces[i], Piece(name, i), token));
                joined.Add((name, media, Join(name, pieces.Count)));
            }
            Finish(joined);
        }
        catch (Exception) when (ct.IsCancellationRequested) { /* paused or removed */ }
        catch (Exception e)
        {
            Record.Status = DownloadStatus.Failed;
            Record.Error = e.Message;
            Notify(true);
        }
    }

    async Task GetAsync(Uri url, string file, CancellationToken ct)
    {
        if (File.Exists(file)) { Count(new FileInfo(file).Length); return; } // already downloaded before a pause
        var data = await FetchAsync(url, ct);
        await File.WriteAllBytesAsync(file + ".tmp", data, ct);
        File.Move(file + ".tmp", file, true);
        Count(data.Length);
        var a = limiter.Consume(data.Length);
        var b = OwnLimiter.Consume(data.Length);
        var wait = a > b ? a : b;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
    }

    void Count(long bytes)
    {
        lock (gate)
        {
            Record.StreamDone = (Record.StreamDone ?? 0) + 1;
            Record.StreamBytes = (Record.StreamBytes ?? 0) + bytes;
        }
        Notify();
    }

    string Join(string track, int n)
    {
        var output = Path.Combine(PartsFolder, track + ".joined");
        using var o = new FileStream(output, FileMode.Create);
        for (var i = 0; i < n; i++)
        {
            using var input = File.OpenRead(Piece(track, i));
            input.CopyTo(o);
        }
        return output;
    }

    static string Ext(string name, HLS.Media m)
    {
        if (m.IsFragmentedMp4) return name == "audio" ? ".m4a" : ".mp4";
        var e = Path.GetExtension(m.Segments[0].AbsolutePath).ToLowerInvariant();
        return name == "audio" && e == ".aac" ? ".aac" : ".ts";
    }

    void Finish(List<(string Name, HLS.Media Media, string File)> joined)
    {
        var first = joined[0];
        // A single HLS rendition (including SoundCloud's fMP4) can be audio-only.
        // Inspect actual streams; track labels and URL extensions are not reliable.
        var info = FFmpeg.Probe(first.File);
        if (info == null)
            throw new HLS.Failure("Cannot inspect HLS media. FFmpeg is required to finalize this stream.");

        var audioOnly = joined.Count == 1 && info.HasAudio && !info.HasVideo;
        var folder = Record.Folder;
        if (sortIntoCategories)
            folder = Path.Combine(folder, (audioOnly ? FileCategory.Music : FileCategory.Video).ToString());
        Directory.CreateDirectory(folder);
        var baseName = Path.GetFileNameWithoutExtension(Record.FileName);
        string saved;
        if (audioOnly)
        {
            // Always remux — even a single fragmented-MP4 rendition needs a seekable index.
            // Stream copy preserves the exact source audio codec and quality.
            var output = FileNames.Unique(baseName + info.AudioExtension, folder);
            if (!FFmpeg.RemuxAudio(first.File, output))
            {
                try { File.Delete(output); } catch { }
                throw new HLS.Failure("Could not finalize the HLS audio without re-encoding. Original pieces were retained.");
            }
            saved = output;
        }
        else
        {
            if (!info.HasVideo)
                throw new HLS.Failure("The downloaded HLS playlist contains no playable video or audio.");
            var output = FileNames.Unique(baseName + ".mp4", folder);
            // Always remux fMP4 so the output has a valid index and can seek.
            // For multiple tracks, the original mux logic still joins picture and sound.
            if (!FFmpeg.Mux(joined.Select(j => j.File).ToList(), output))
            {
                try { File.Delete(output); } catch { }
                throw new HLS.Failure("Could not finalize the HLS video without re-encoding. Original pieces were retained.");
            }
            saved = output;
        }

        try { Directory.Delete(PartsFolder, true); } catch { }
        Record.SavedPath = saved;
        Record.FileName = Path.GetFileName(saved);
        Record.Size = new FileInfo(saved).Length;
        Record.Status = DownloadStatus.Finished;
        Record.Finished = DateTime.Now;
        Notify(true);
        http.Dispose();
    }
}
