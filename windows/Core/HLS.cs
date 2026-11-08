using System.Diagnostics;

namespace Nexonic.Core;

/// <summary>Streamed video sent as a playlist of small pieces (HLS, .m3u8).</summary>
public static class HLS
{
    public sealed record Variant(Uri Url, long Bandwidth, string? Resolution, string? AudioGroup)
    {
        public long Pixels
        {
            get
            {
                var p = (Resolution ?? "").ToLowerInvariant().Split('x');
                return p.Length == 2 && long.TryParse(p[0], out var w) && long.TryParse(p[1], out var h) ? w * h : 0;
            }
        }
    }

    public sealed class Media
    {
        public List<Uri> Segments { get; } = new();
        public Uri? InitSegment { get; set; }
        /// <summary>"NONE" or the encryption method the stream uses (protected streams are not saved).</summary>
        public string Encryption { get; set; } = "NONE";
        public bool IsFragmentedMp4 => InitSegment != null;
    }

    public sealed class Playlist
    {
        public List<Variant> Variants { get; } = new();
        public Dictionary<string, Uri> Audio { get; } = new();
        public Media? Media { get; set; }
        public bool IsMaster => Media == null;
    }

    public sealed class Failure(string message) : Exception(message);

    public static Dictionary<string, string> Attributes(string line)
    {
        var result = new Dictionary<string, string>();
        var colon = line.IndexOf(':');
        if (colon < 0) return result;
        var key = new System.Text.StringBuilder();
        var value = new System.Text.StringBuilder();
        bool inKey = true, quoted = false;
        foreach (var ch in line[(colon + 1)..])
        {
            if (inKey) { if (ch == '=') inKey = false; else key.Append(ch); }
            else if (ch == '"') quoted = !quoted;
            else if (ch == ',' && !quoted)
            {
                result[key.ToString().Trim().ToUpperInvariant()] = value.ToString();
                key.Clear(); value.Clear(); inKey = true;
            }
            else value.Append(ch);
        }
        if (key.Length > 0) result[key.ToString().Trim().ToUpperInvariant()] = value.ToString();
        return result;
    }

    public static Playlist Parse(string text, Uri baseUri)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0 || !lines[0].StartsWith("#EXTM3U")) throw new Failure("This link is not a video playlist");
        Uri? Resolve(string s) => Uri.TryCreate(baseUri, s, out var u) ? u : null;
        var p = new Playlist();
        if (lines.Any(l => l.StartsWith("#EXT-X-STREAM-INF")))
        {
            Dictionary<string, string>? pending = null;
            foreach (var line in lines)
            {
                if (line.StartsWith("#EXT-X-STREAM-INF")) pending = Attributes(line);
                else if (line.StartsWith("#EXT-X-MEDIA"))
                {
                    var a = Attributes(line);
                    if (a.GetValueOrDefault("TYPE") == "AUDIO" && a.TryGetValue("GROUP-ID", out var g) && a.TryGetValue("URI", out var uri)
                        && Resolve(uri) is { } u && (!p.Audio.ContainsKey(g) || a.GetValueOrDefault("DEFAULT") == "YES"))
                        p.Audio[g] = u;
                }
                else if (!line.StartsWith('#') && pending != null && Resolve(line) is { } u)
                {
                    p.Variants.Add(new Variant(u, long.TryParse(pending.GetValueOrDefault("BANDWIDTH"), out var b) ? b : 0,
                        pending.GetValueOrDefault("RESOLUTION"), pending.GetValueOrDefault("AUDIO")));
                    pending = null;
                }
            }
            if (p.Variants.Count == 0) throw new Failure("The video playlist is empty");
            return p;
        }
        var m = new Media();
        foreach (var line in lines)
        {
            if (line.StartsWith("#EXT-X-KEY") || line.StartsWith("#EXT-X-SESSION-KEY"))
            {
                var method = Attributes(line).GetValueOrDefault("METHOD") ?? "NONE";
                if (method != "NONE") m.Encryption = method;
            }
            else if (line.StartsWith("#EXT-X-MAP")) m.InitSegment = Attributes(line).TryGetValue("URI", out var i) ? Resolve(i) : null;
            else if (!line.StartsWith('#') && Resolve(line) is { } u) m.Segments.Add(u);
        }
        if (m.Segments.Count == 0) throw new Failure("The video playlist is empty");
        p.Media = m;
        return p;
    }

    /// <summary>The best picture: most pixels, then the highest bit rate.</summary>
    public static Variant? Best(IEnumerable<Variant> variants) =>
        variants.OrderByDescending(v => v.Pixels).ThenByDescending(v => v.Bandwidth).FirstOrDefault();

    /// <summary>Never promise an MP4 container before the HLS tracks have been probed.</summary>
    public static string PendingFileName(string fileName)
    {
        var ext = System.IO.Path.GetExtension(fileName);
        return ext.Equals(".m3u8", StringComparison.OrdinalIgnoreCase) || ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            ? System.IO.Path.GetFileNameWithoutExtension(fileName) : fileName;
    }

    public static bool IsPlaylistUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Bundled FFmpeg tools. On Windows only the ffmpeg folder next to the app is used.</summary>
public static class FFmpeg
{
    public static string BundledFolder => System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg");

    public static string? Path
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                var bundled = System.IO.Path.Combine(BundledFolder, "ffmpeg.exe");
                return File.Exists(bundled) ? bundled : null;
            }
            var dirs = new List<string> { AppContext.BaseDirectory };
            dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
            return dirs.Select(d => System.IO.Path.Combine(d, "ffmpeg")).FirstOrDefault(File.Exists);
        }
    }

    public static string? ProbePath
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                var bundled = System.IO.Path.Combine(BundledFolder, "ffprobe.exe");
                return File.Exists(bundled) ? bundled : null;
            }
            if (Path is not { } ffmpeg) return null;
            var probe = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ffmpeg)!, "ffprobe");
            return File.Exists(probe) ? probe : null;
        }
    }

    public sealed record MediaInfo(bool HasVideo, bool HasAudio, string? AudioCodec)
    {
        /// <summary>Use the original encoded audio without quality loss, in an appropriate seekable container.</summary>
        public string AudioExtension => AudioCodec?.ToLowerInvariant() switch
        {
            "aac" or "alac" => ".m4a",
            "mp3" => ".mp3",
            "opus" => ".opus",
            "vorbis" => ".ogg",
            "flac" => ".flac",
            _ => ".mka",
        };
    }

    /// <summary>Probe joined HLS fragments rather than guessing whether an audio-only stream is video.</summary>
    public static MediaInfo? Probe(string input)
    {
        if (Path is not { } ffmpeg) return null;
        try
        {
            if (ProbePath is { } probe)
            {
                var psi = new ProcessStartInfo(probe)
                {
                    CreateNoWindow = true, UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                foreach (var a in new[] { "-v", "error", "-show_entries", "stream=codec_type,codec_name", "-of", "json", input })
                    psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                var output = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode == 0)
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(output);
                    var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToArray();
                    var video = streams.Any(s => s.TryGetProperty("codec_type", out var t) && t.GetString() == "video");
                    var audio = streams.FirstOrDefault(s => s.TryGetProperty("codec_type", out var t) && t.GetString() == "audio");
                    if (video || audio.ValueKind != System.Text.Json.JsonValueKind.Undefined)
                    {
                        var codec = audio.ValueKind != System.Text.Json.JsonValueKind.Undefined &&
                            audio.TryGetProperty("codec_name", out var n) ? n.GetString() : null;
                        return new MediaInfo(video, audio.ValueKind != System.Text.Json.JsonValueKind.Undefined, codec);
                    }
                }
            }

            // ffprobe is optional in older installs; ffmpeg itself prints stream metadata without transcoding.
            var fallback = new ProcessStartInfo(ffmpeg)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in new[] { "-hide_banner", "-i", input }) fallback.ArgumentList.Add(a);
            using var process = Process.Start(fallback)!;
            process.StandardOutput.ReadToEnd();
            var report = process.StandardError.ReadToEnd();
            process.WaitForExit();
            var hasVideo = System.Text.RegularExpressions.Regex.IsMatch(report, @"Stream #\d+:\d+(?:\[[^\]]+\])?(?:\([^)]*\))?: Video:");
            var audioMatch = System.Text.RegularExpressions.Regex.Match(report, @"Stream #\d+:\d+(?:\[[^\]]+\])?(?:\([^)]*\))?: Audio: ([\w]+)");
            return hasVideo || audioMatch.Success
                ? new MediaInfo(hasVideo, audioMatch.Success, audioMatch.Success ? audioMatch.Groups[1].Value : null)
                : null;
        }
        catch { return null; }
    }

    /// <summary>Only audio-only MP4 files need a lossless conversion to an audio container.</summary>
    public static bool NeedsAudioRemux(string filename, MediaInfo? media) =>
        System.IO.Path.GetExtension(filename).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
        && media is { HasAudio: true, HasVideo: false };

    /// <summary>Rebuild seekable audio output with stream copy (no re-encoding).</summary>
    public static bool RemuxAudio(string input, string output)
    {
        if (Path is not { } exe) return false;
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in new[] { "-y", "-v", "error", "-i", input, "-map", "0:a:0", "-vn", "-c:a", "copy" })
                psi.ArgumentList.Add(a);
            if (System.IO.Path.GetExtension(output).Equals(".m4a", StringComparison.OrdinalIgnoreCase))
            {
                psi.ArgumentList.Add("-movflags");
                psi.ArgumentList.Add("+faststart");
            }
            psi.ArgumentList.Add(output);
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0;
        }
        catch { return false; }
    }

    public static bool Mux(IReadOnlyList<string> inputs, string output)
    {
        if (Path is not { } exe) return false;
        try
        {
            var psi = new ProcessStartInfo(exe) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "-y", "-loglevel", "error" }) psi.ArgumentList.Add(a);
            foreach (var i in inputs) { psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(i); }
            for (var n = 0; n < inputs.Count; n++) { psi.ArgumentList.Add("-map"); psi.ArgumentList.Add(n.ToString()); }
            foreach (var a in new[] { "-c", "copy", "-movflags", "+faststart", output }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
