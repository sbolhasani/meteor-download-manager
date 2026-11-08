using System.Net;
using System.Net.Http.Headers;

namespace Nexonic.Core;

/// <summary>
/// One download: probes the server, splits the file into byte ranges and downloads them in parallel straight into one
/// file next to where it will be saved ("name.nxpart"), splits the largest remaining range whenever a connection frees
/// up and retries failed ranges. When everything has arrived the file is only renamed, so even huge downloads finish
/// at once and never need twice their size on disk. Downloads started by versions before 0.19 keep their separate
/// part files and are joined at the end as before.
/// </summary>
public sealed class Transfer : IDownloader
{
    public DownloadRecord Record { get; }
    public event Action<DownloadRecord>? Changed;

    readonly int connections;
    readonly SpeedLimiter limiter;
    /// <summary>This download's own limit, on top of the shared one (0 = none).</summary>
    public SpeedLimiter OwnLimiter { get; } = new();
    readonly bool sortIntoCategories;
    readonly HttpClient http;
    readonly Uri origin;
    CancellationTokenSource? cts;
    DateTime lastNotify = DateTime.MinValue;

    string PartsFolder => Path.Combine(AppPaths.Parts, Record.Id.ToString("N"));
    string PartPath(int id) => Path.Combine(PartsFolder, $"{id}.part");
    /// <summary>Written straight into one file (0.19+), or into one part file per range (started by an older version).</summary>
    bool SingleFile => Record.WorkFile != null;

    /// <summary>Removes what an unfinished download left on disk: the part files and the "name.nxpart" file.</summary>
    public static void DeleteTemporaryFiles(DownloadRecord r)
    {
        try { Directory.Delete(Path.Combine(AppPaths.Parts, r.Id.ToString("N")), true); } catch { }
        if (r.Status != DownloadStatus.Finished && r.WorkFile is { } work)
        {
            try { File.Delete(work); } catch { }
            r.WorkFile = null;
        }
    }

    string TargetFolder()
    {
        var folder = Record.Folder;
        if (sortIntoCategories && FileCategories.Folder(Record.Category) is { } sub) folder = Path.Combine(folder, sub);
        return folder;
    }

    readonly TransferOptions options;

    public Transfer(DownloadRecord record, int connections, SpeedLimiter limiter, bool sortIntoCategories = false, ProxySettings? proxy = null,
        TransferOptions? options = null)
    {
        this.options = options ?? new TransferOptions(Retries: Network.Options.Retries, ReadTimeoutSeconds: Network.Options.ReadTimeoutSeconds);
        Record = record;
        this.connections = Math.Clamp(connections, 1, 32);
        this.limiter = limiter;
        this.sortIntoCategories = sortIntoCategories;
        origin = new Uri(record.Url);
        OwnLimiter.SetLimit((record.SpeedLimitKBps ?? 0) * 1024);
        var handler = Network.Handler(proxy ?? new ProxySettings());
        if (!string.IsNullOrEmpty(record.Username))
        {
            // The login is only ever offered to the download's own server, never to a host it redirects to.
            handler.Credentials = Network.CredentialsFor(origin, new NetworkCredential(record.Username, record.Password ?? ""));
            handler.PreAuthenticate = true;
        }
        http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
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
        DeleteTemporaryFiles(Record);
    }

    async Task RunAsync(CancellationToken ct)
    {
        try
        {
            Record.Status = DownloadStatus.Probing;
            Record.Error = null;
            Notify(true);
            await ProbeAsync(ct);
            Record.Status = DownloadStatus.Downloading;
            Notify(true);
            await DownloadAllAsync(ct);
            if (SingleFile) Finish(); else Join();
        }
        catch (Exception) when (ct.IsCancellationRequested) { /* paused or removed */ }
        catch (Exception e)
        {
            if (e is FileChangedException)
            {
                // Never mix two versions of a file: the next Resume starts from the beginning.
                lock (Record.Segments) Record.Segments = new();
                Record.Validator = null;
                DeleteTemporaryFiles(Record);
            }
            Record.Status = DownloadStatus.Failed;
            Record.Error = e.Message;
            Notify(true);
        }
    }

    Task<HttpResponseMessage> SendAsync(string? range, CancellationToken ct, string? ifRange = null) =>
        Network.SendAsync(http, origin, url =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            if (range != null) req.Headers.TryAddWithoutValidation("Range", range);
            if (range != null && ifRange != null) req.Headers.TryAddWithoutValidation("If-Range", ifRange);
            if (!string.IsNullOrEmpty(Record.Referrer)) req.Headers.TryAddWithoutValidation("Referer", Record.Referrer);
            if (!string.IsNullOrEmpty(Record.UserAgent)) req.Headers.TryAddWithoutValidation("User-Agent", Record.UserAgent);
            Network.AddPrivateHeaders(req, url, origin, Record.Cookies, null, null);
            return req;
        }, HttpCompletionOption.ResponseHeadersRead, ct);

    async Task ProbeAsync(CancellationToken ct)
    {
        using var res = await SendAsync("bytes=0-0", ct); // headers only; the body is never read
        var code = (int)res.StatusCode;
        if (code == 401) throw new InvalidOperationException("The server asks for a user name and password (use Properties → Login)");
        if (code == 407) throw new InvalidOperationException("The proxy asks for a user name and password (Options → Connection)");
        if (code is 403 or 404 or 410 && Record.Segments.Count > 0)
            throw new InvalidOperationException($"The link has expired (HTTP {code}). Use Refresh Link to continue from where it stopped");
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"The server answered HTTP {code}");
        long? size = null;
        var ranges = false;
        if (res.StatusCode == HttpStatusCode.PartialContent && res.Content.Headers.ContentRange?.Length is long total)
        {
            size = total;
            ranges = true;
        }
        else if (res.Content.Headers.ContentLength is > 0 and var len) size = len;

        var validator = Network.Validator(res);
        var cd = res.Content.Headers.ContentDisposition?.ToString();
        if (FileNames.FromContentDisposition(cd) is { } name && Record.FileName == FileNames.FromUrl(Record.Url)) Record.FileName = name;

        // Resume only into the same file: same size and, when the server names its version, the same ETag/Last-Modified.
        var canResume = Record.Segments.Count > 0 && ranges && Record.AcceptsRanges && size == Record.Size
                        && Network.SameVersion(Record.Validator, validator);
        if (canResume && SingleFile && !File.Exists(Record.WorkFile)) canResume = false;  // the .nxpart file was deleted
        if (canResume && !SingleFile && !Directory.Exists(PartsFolder)) canResume = false;
        if (canResume && SingleFile)
        {
            // The last bytes written before the app stopped may not have reached the disk: fetch them again.
            lock (Record.Segments)
                foreach (var s in Record.Segments) s.Written = Math.Max(0, s.Written - Math.Min(s.Written, ResumeOverlap));
            if (Record.Size is long full)
            {
                long have;
                lock (Record.Segments) have = Record.Segments.Sum(s => s.Written);
                if (DiskSpace.Problem(Path.GetDirectoryName(Record.WorkFile)!, full, have) is { } problem)
                    throw new InvalidOperationException(problem);
            }
        }
        else if (canResume)
        {
            foreach (var s in Record.Segments)
            {
                var onDisk = File.Exists(PartPath(s.Id)) ? new FileInfo(PartPath(s.Id)).Length : 0;
                s.Written = Math.Min(onDisk, s.Length ?? onDisk);
                using var f = new FileStream(PartPath(s.Id), FileMode.OpenOrCreate);
                f.SetLength(s.Written);
            }
        }
        else
        {
            DeleteTemporaryFiles(Record);
            var folder = TargetFolder();
            Directory.CreateDirectory(folder);
            if (size is long need && DiskSpace.Problem(folder, need) is { } problem) throw new InvalidOperationException(problem);
            var work = FileNames.Unique(Record.FileName + WorkSuffix, folder);
            using (var f = new FileStream(work, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite)) DiskSpace.MakeSparse(f);
            Record.WorkFile = work;
            Record.Size = size;
            Record.AcceptsRanges = ranges;
            Record.Validator = validator;
            lock (Record.Segments)
            {
                Record.Segments = ranges
                    ? SegmentPlanner.Plan(size, connections, options.MinSegment)
                    : new() { new Segment { Id = 0, Start = 0, End = size is > 0 ? size.Value - 1 : -1 } };
            }
        }
    }

    async Task DownloadAllAsync(CancellationToken outer)
    {
        // One failing part stops its siblings, and they are awaited before anything is cleaned up or reported.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var ct = stop.Token;
        var running = new List<Task>();
        List<Segment> pending;
        lock (Record.Segments) pending = Record.Segments.Where(s => !s.IsComplete).ToList();
        foreach (var s in pending) running.Add(SegmentAsync(s, ct));
        while (running.Count > 0)
        {
            var done = await Task.WhenAny(running);
            running.Remove(done);
            if (done.IsFaulted || (done.IsCanceled && !outer.IsCancellationRequested))
            {
                stop.Cancel();
                try { await Task.WhenAll(running); } catch { /* stopped on purpose */ }
            }
            await done; // rethrows a final failure
            if (!Record.AcceptsRanges || running.Count >= connections) continue;
            Segment? added = null;
            lock (Record.Segments)
            {
                var split = SegmentPlanner.SplitLargest(Record.Segments, Record.Segments.Max(s => s.Id) + 1, options.MinSegment);
                if (split is { } sp)
                {
                    Record.Segments[sp.Index].End = sp.NewEnd;
                    Record.Segments.Add(sp.Added);
                    added = sp.Added;
                }
            }
            if (added != null) running.Add(SegmentAsync(added, ct));
        }
    }

    async Task SegmentAsync(Segment seg, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        long writtenAtStart = -1;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Record.AcceptsRanges && writtenAtStart >= 0 && seg.Written > writtenAtStart) attempt = 1; // kept progress: fresh retries
                writtenAtStart = seg.Written;
                if (!Record.AcceptsRanges) seg.Written = 0;
                string? range = null;
                var from = seg.Start + seg.Written;
                if (Record.AcceptsRanges) range = seg.End >= 0 ? $"bytes={from}-{seg.End}" : $"bytes={from}-";
                using var res = await SendAsync(range, ct, Network.IfRange(Record.Validator));
                if (Record.AcceptsRanges && res.StatusCode == HttpStatusCode.OK && Network.IfRange(Record.Validator) != null)
                    throw new FileChangedException();
                var ok = res.StatusCode == HttpStatusCode.PartialContent || (res.StatusCode == HttpStatusCode.OK && !Record.AcceptsRanges);
                if (!ok) throw new HttpRequestException($"HTTP {(int)res.StatusCode}");
                if (res.StatusCode == HttpStatusCode.PartialContent && !Network.RangeMatches(res.Content.Headers.ContentRange, from, Record.Size))
                    throw new IOException("The server sent a different part of the file than asked for");
                await using var body = await res.Content.ReadAsStreamAsync(ct);
                await using var file = new FileStream(SingleFile ? Record.WorkFile! : PartPath(seg.Id), FileMode.OpenOrCreate,
                    FileAccess.Write, FileShare.ReadWrite, 1 << 16, true);
                if (SingleFile)
                {
                    // Every range writes its own place in the one file; only a download without ranges starts it over.
                    if (!Record.AcceptsRanges) file.SetLength(0);
                    file.Seek(seg.Start + seg.Written, SeekOrigin.Begin);
                }
                else
                {
                    file.SetLength(seg.Written);
                    file.Seek(seg.Written, SeekOrigin.Begin);
                }
                // A connection that stays silent is dropped and tried again instead of waiting forever.
                using var silent = CancellationTokenSource.CreateLinkedTokenSource(ct);
                while (true)
                {
                    long left;
                    lock (Record.Segments) left = seg.Remaining;
                    if (left <= 0) return; // finished (possibly shortened by a split)
                    int n;
                    silent.CancelAfter(TimeSpan.FromSeconds(options.ReadTimeoutSeconds));
                    try { n = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), silent.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new IOException($"No data received for {options.ReadTimeoutSeconds} seconds");
                    }
                    if (n == 0)
                    {
                        if (seg.End < 0)
                        {
                            lock (Record.Segments) seg.End = seg.Start + seg.Written - 1;
                            Record.Size = seg.Written;
                            return;
                        }
                        throw new IOException("The connection closed early");
                    }
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                    lock (Record.Segments) seg.Written += n;
                    Notify();
                    var a = limiter.Consume(n);
                    var b = OwnLimiter.Consume(n);
                    var wait = a > b ? a : b;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                }
            }
            catch (Exception e) when (e is not FileChangedException && !ct.IsCancellationRequested
                                      && (attempt < options.Retries || (Record.AcceptsRanges && seg.Written > writtenAtStart)))
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt - 1))), ct);
            }
        }
    }

    /// <summary>The in-progress file's name ends with this until the download is complete.</summary>
    public const string WorkSuffix = ".nxpart";
    const long ResumeOverlap = 256 * 1024;

    /// <summary>Everything has arrived in the .nxpart file: check its size and give it its real name.</summary>
    void Finish()
    {
        var work = Record.WorkFile!;
        var length = new FileInfo(work).Length;
        if (Record.Size is long size)
        {
            if (length < size)
            {
                // Never keep a short file: the next Resume downloads it again from the start.
                lock (Record.Segments) Record.Segments = new();
                DeleteTemporaryFiles(Record);
                throw new IOException($"Size check failed: expected {size} bytes, got {length}. Resume to download it again");
            }
            if (length > size) using (var f = new FileStream(work, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) f.SetLength(size);
        }
        var target = FileNames.Unique(Record.FileName, Path.GetDirectoryName(work)!);
        File.Move(work, target);
        Record.WorkFile = null;
        try { Directory.Delete(PartsFolder, true); } catch { }
        Complete(target);
    }

    sealed class FileChangedException() : Exception(
        "The file changed on the server since this download started. Resume to download the new version from the start");

    /// <summary>Downloads started before 0.19: copies the part files into the final file, showing progress.</summary>
    void Join()
    {
        var folder = TargetFolder();
        Directory.CreateDirectory(folder);
        var total = Record.Size ?? Record.Segments.Sum(s => s.Written);
        if (DiskSpace.Problem(folder, total) is { } problem) throw new InvalidOperationException(problem);
        var target = FileNames.Unique(Record.FileName, folder);
        try
        {
            using var output = new FileStream(target, FileMode.CreateNew);
            var buffer = new byte[1 << 20];
            long copied = 0;
            foreach (var s in Record.Segments.OrderBy(s => s.Start))
            {
                using var input = File.OpenRead(PartPath(s.Id));
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (cts?.IsCancellationRequested == true) throw new OperationCanceledException();
                    output.Write(buffer, 0, n);
                    copied += n;
                    Record.VideoStep = total > 0 ? $"Joining parts… {copied * 100 / total}%" : "Joining parts…";
                    Notify();
                }
            }
            if (Record.Size is long size && output.Length != size)
                throw new IOException($"Size check failed: expected {size} bytes, got {output.Length}");
        }
        catch
        {
            // Keep the parts so Resume can try again, and never leave a half-written file behind.
            try { File.Delete(target); } catch { }
            Record.VideoStep = null;
            throw;
        }
        Record.VideoStep = null;
        try { Directory.Delete(PartsFolder, true); } catch { }
        Complete(target);
    }

    void Complete(string target)
    {
        var hasChecksum = Checksum.Parse(Record.Checksum) != null;
        if (hasChecksum)
        {
            Record.ChecksumResult = Checksum.Verify(Record.Checksum!, target);
            if (Record.ChecksumResult.Contains("does not match"))
                Record.Error = "The checksum does not match: the file may be damaged or not the one expected";
        }

        // Direct HTTP downloads can also be audio-only MP4 (not HLS, not a site-engine job).
        // Detect the actual contained tracks and copy audio losslessly to a seekable container.
        // Never change files with a user-supplied checksum: it must refer to the original bytes.
        if (!hasChecksum && System.IO.Path.GetExtension(target).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            var media = FFmpeg.Probe(target);
            if (FFmpeg.NeedsAudioRemux(target, media))
            {
                var output = FileNames.Unique(System.IO.Path.GetFileNameWithoutExtension(target) + media!.AudioExtension,
                    System.IO.Path.GetDirectoryName(target)!);
                if (FFmpeg.RemuxAudio(target, output))
                {
                    // Only discard the original after a nonempty, successful remux.
                    try { File.Delete(target); } catch { }
                    target = output;
                }
                else
                {
                    try { File.Delete(output); } catch { }
                    Record.Error = "Audio was saved unchanged because lossless remux failed. Check FFmpeg.";
                }
            }
        }
        Record.SavedPath = target;
        Record.FileName = Path.GetFileName(target);
        Record.Status = DownloadStatus.Finished;
        Record.Finished = DateTime.Now;
        Notify(true);
        http.Dispose();
    }

    void Notify(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && (now - lastNotify).TotalMilliseconds < 250) return;
        lastNotify = now;
        Changed?.Invoke(Record);
    }
}

public static class Network
{
    /// <summary>Advanced connection settings from Options (the app sets them when it starts and when they change).</summary>
    public static NetworkOptions Options { get; set; } = new();

    /// <summary>Same server and no step down from https: cookies and logins may be sent there.</summary>
    public static bool SameSite(Uri url, Uri origin) =>
        string.Equals(url.Host, origin.Host, StringComparison.OrdinalIgnoreCase) && url.Port == origin.Port
        && !(origin.Scheme == Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttps);

    /// <summary>Cookies and a Basic login, only for the download's own server.</summary>
    public static void AddPrivateHeaders(HttpRequestMessage req, Uri url, Uri origin, string? cookies, string? user, string? password)
    {
        if (!SameSite(url, origin)) return;
        if (!string.IsNullOrEmpty(cookies)) req.Headers.TryAddWithoutValidation("Cookie", cookies);
        if (!string.IsNullOrEmpty(user))
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{password}")));
    }

    /// <summary>A login that only answers challenges from the download's own server.</summary>
    public static ICredentials CredentialsFor(Uri origin, NetworkCredential login)
    {
        var cache = new CredentialCache();
        var prefix = new Uri(origin.GetLeftPart(UriPartial.Authority));
        foreach (var scheme in new[] { "Basic", "Digest", "NTLM", "Negotiate" }) cache.Add(prefix, scheme, login);
        return cache;
    }

    /// <summary>
    /// Sends a request and follows redirects by hand (at most 10, http/https only). `build` makes the request for each
    /// address, so private headers are decided per hop and never reach another host.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, Uri start, Func<Uri, HttpRequestMessage> build,
        HttpCompletionOption option, CancellationToken ct)
    {
        var url = start;
        for (var hop = 0; hop < 10; hop++)
        {
            using var req = build(url);
            var res = await http.SendAsync(req, option, ct);
            var code = (int)res.StatusCode;
            if (code is >= 300 and < 400 && res.Headers.Location is { } loc)
            {
                url = loc.IsAbsoluteUri ? loc : new Uri(url, loc);
                res.Dispose();
                if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidOperationException("Redirect to an unsupported address");
                continue;
            }
            return res;
        }
        throw new InvalidOperationException("Too many redirects");
    }

    /// <summary>The file's version as the server names it: a strong ETag, else Last-Modified (null if neither).</summary>
    public static string? Validator(HttpResponseMessage res)
    {
        if (res.Headers.ETag is { IsWeak: false } tag) return tag.Tag;
        if (res.Content.Headers.LastModified is { } lm) return lm.ToString("R");
        return null;
    }

    /// <summary>True unless both versions are known and differ (older lists have none: size decides as before).</summary>
    public static bool SameVersion(string? saved, string? now) => saved == null || now == null || saved == now;

    /// <summary>The If-Range value: the server then answers 200 with the whole file if it changed, never a wrong part.</summary>
    public static string? IfRange(string? validator) => string.IsNullOrEmpty(validator) ? null : validator;

    /// <summary>A 206 answer must start where we asked and belong to a file of the expected size.</summary>
    public static bool RangeMatches(ContentRangeHeaderValue? cr, long from, long? size) =>
        cr is { From: long f } && f == from && (size is not long s || cr.Length is not long total || total == s);

    /// <summary>Connection settings with the chosen proxy. Redirects are followed by hand so cookies never leave the original host.</summary>
    public static SocketsHttpHandler Handler(ProxySettings p)
    {
        var h = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = 32,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Clamp(Options.ConnectTimeoutSeconds, 3, 300)),
        };
        // DNS over HTTPS for direct connections (with a proxy, the proxy looks the name up).
        var doh = Options.DnsOverHttps.Trim();
        if (NetworkOptions.DnsOverHttpsUrlOk(doh) && (p.Mode is ProxyMode.None or ProxyMode.System) && (p.Mode == ProxyMode.None || !HasSystemProxy()))
        {
            var timeout = h.ConnectTimeout;
            h.ConnectCallback = (ctx, ct) => DnsOverHttps.ConnectAsync(doh, ctx.DnsEndPoint, timeout, ct);
        }
        var host = p.Host.Trim();
        switch (p.Mode)
        {
            case ProxyMode.None:
                h.UseProxy = false;
                break;
            case ProxyMode.Http or ProxyMode.Socks when host.Length > 0:
                var scheme = p.Mode == ProxyMode.Socks ? "socks5" : "http";
                var proxy = new WebProxy(new Uri($"{scheme}://{host}:{p.Port}"));
                if (p.Username.Length > 0) proxy.Credentials = new NetworkCredential(p.Username, p.Password);
                h.Proxy = proxy;
                h.UseProxy = true;
                break;
        }
        return h;
    }

    static bool HasSystemProxy()
    {
        try { return HttpClient.DefaultProxy.GetProxy(new Uri("https://example.com")) is { } u && u.Host != "example.com"; }
        catch { return false; }
    }
}
