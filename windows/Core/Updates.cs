using System.Text;
using System.Text.Json;

namespace Nexonic.Core;

/// <summary>Finds out whether a newer version is published on GitHub Releases.</summary>
public static class Updates
{
    public const string LatestUrl = "https://api.github.com/repos/sbolhasani/meteor-download-manager/releases/latest";
    /// <summary>The same details as a file attached to every release. Used when GitHub's API refuses (it allows 60
    /// checks an hour per network, which shared office and mobile networks use up).</summary>
    public const string MirrorUrl = "https://github.com/sbolhasani/meteor-download-manager/releases/latest/download/latest.json";

    public sealed record Release(string Version, string Notes, string Page, IReadOnlyDictionary<string, string> Assets,
        IReadOnlyDictionary<string, string>? Digests = null);

    /// <summary>Only files attached to this app's own GitHub releases are ever downloaded as updates.</summary>
    public static bool TrustedAsset(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.Host == "github.com"
        && u.AbsolutePath.StartsWith("/sbolhasani/meteor-download-manager/releases/download/", StringComparison.Ordinal);

    /// <summary>The SHA-256 GitHub publishes for an attached file, or null.</summary>
    public static string? Digest(Release r, string asset) =>
        r.Digests != null && r.Digests.TryGetValue(asset, out var d) && d.Length == 64 ? d : null;

    /// <summary>True when the downloaded file is exactly the one GitHub published.</summary>
    public static bool Verify(string path, string sha256) =>
        string.Equals(Checksum.Compute(ChecksumAlgorithm.Sha256, path), sha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the GitHub "latest release" answer.</summary>
    public static Release? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var o = doc.RootElement;
            if (!o.TryGetProperty("tag_name", out var tag) || !o.TryGetProperty("html_url", out var page)) return null;
            if (o.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
            if (o.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
            var assets = new Dictionary<string, string>();
            var digests = new Dictionary<string, string>();
            if (o.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var a in list.EnumerateArray())
                    if (a.TryGetProperty("name", out var n) && a.TryGetProperty("browser_download_url", out var u))
                    {
                        assets[n.GetString() ?? ""] = u.GetString() ?? "";
                        // GitHub's own checksum of the file ("sha256:…")
                        if (a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String
                            && dg.GetString() is { } ds && ds.StartsWith("sha256:", StringComparison.Ordinal))
                            digests[n.GetString() ?? ""] = ds["sha256:".Length..].ToLowerInvariant();
                    }
            var version = (tag.GetString() ?? "").TrimStart('v');
            var body = o.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
            return new Release(version, body, page.GetString() ?? "", assets, digests);
        }
        catch { return null; }
    }

    /// <summary>True when <paramref name="a"/> is a higher version than <paramref name="b"/> ("0.6.0" > "0.5.9", "0.10.0" > "0.9.1").</summary>
    public static bool IsNewer(string a, string b)
    {
        static int[] Parts(string s) => s.Split('.').Select(x => int.TryParse(new string(x.TakeWhile(char.IsDigit).ToArray()), out var v) ? v : 0).ToArray();
        var x = Parts(a);
        var y = Parts(b);
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var l = i < x.Length ? x[i] : 0;
            var r = i < y.Length ? y[i] : 0;
            if (l != r) return l > r;
        }
        return false;
    }

    /// <summary>The release notes of every version newer than the one installed ("## 0.6.0" sections), without Markdown marks.</summary>
    public static string Notes(string body, string current)
    {
        var sb = new StringBuilder();
        var keep = false;
        foreach (var raw in body.Replace("\r", "").Split('\n'))
        {
            if (raw.StartsWith("## "))
            {
                var v = raw[3..].Trim();
                keep = IsNewer(v, current);
                if (keep) sb.Append(sb.Length == 0 ? "" : "\n\n").Append("Version ").Append(v);
                continue;
            }
            if (keep) sb.Append('\n').Append(raw.Replace("**", "").Replace("`", ""));
        }
        var text = sb.ToString().Trim();
        return text.Length > 2500 ? text[..2500] + "…" : text;
    }
}
