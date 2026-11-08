using System.Diagnostics;
using Nexonic.Core;

// Browser helper (native messaging) and the ndm command line: both hand downloads to the app through the inbox.
const string Version = "0.19.1";

if (CommandLineTool.IsCommand(args))
{
    var r = CommandLineTool.Parse(args);
    if (r.Help) { Console.WriteLine(CommandLineTool.Usage); return 0; }
    if (r.Error != null) { Console.Error.WriteLine($"ndm: {r.Error}"); return 2; }
    try
    {
        foreach (var j in r.Jobs) new Inbox().Drop(j);
        StartApp();
        Console.WriteLine($"Added {r.Jobs.Count} download{(r.Jobs.Count == 1 ? "" : "s")}");
        return 0;
    }
    catch (Exception e) { Console.Error.WriteLine($"ndm: {e.Message}"); return 1; }
}

var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();

void StartApp()
{
    // Linux AppImage: start the AppImage itself (this helper's copy of it goes away when the helper exits).
    var appImage = OperatingSystem.IsLinux() ? Environment.GetEnvironmentVariable("APPIMAGE") : null;
    var app = appImage is { Length: > 0 } && File.Exists(appImage) ? appImage
        : Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "MeteorDownloadManager.exe" : "NexonicDownloadManager");
    if (!File.Exists(app)) return;
    // The app is single-instance: a second start only wakes the running one, which then reads the inbox.
    try
    {
        if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(app, "--inbox") { UseShellExecute = false });
        else
        {
            // Detached from the browser's pipes, so the browser never waits on the app.
            var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            foreach (var a in new[] { "-c", "\"$0\" --inbox </dev/null >/dev/null 2>&1 &", app }) psi.ArgumentList.Add(a);
            Process.Start(psi)?.WaitForExit(5000);
        }
    }
    catch { }
}

// The proxy set in the app (its password decrypted for this Windows user), so lookups take the same route as downloads.
// The settings file is only read here, never changed.
static ProxySettings? SavedProxy()
{
    try
    {
        return File.Exists(AppPaths.Settings) ? Secrets.DeserializeOpened<AppSettings>(File.ReadAllText(AppPaths.Settings))?.Proxy : null;
    }
    catch { return null; }
}

// The picture qualities a video site offers for one link, looked up with the video engine shipped next to this helper.
static object Qualities(System.Text.Json.Nodes.JsonNode msg)
{
    string? Text(string k) => msg[k] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    var url = Text("url") ?? "";
    if (!new DownloadJob(url).IsValid || !SiteVideo.IsSupported(url)) return new { ok = false, error = "Not a video site link" };
    var tool = new[] { SiteVideoTools.YtDlp, Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? @"video-engine\nexonic-video-engine.exe" : "nexonic-video-engine") }
        .FirstOrDefault(File.Exists);
    if (tool == null) return new { ok = false, error = "The video engine is missing" };
    try
    {
        var (args, configFile) = SiteVideo.Prepare(
            SiteVideo.InfoArguments(url, SiteVideoTools.QuickJs, false, Text("referrer"), Text("cookies"), Text("userAgent"), null, null), SavedProxy());
        using var cleanup = new ConfigCleanup(configFile);
        var psi = new ProcessStartInfo(tool)
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { };
        p.BeginErrorReadLine();
        var read = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(40_000)) { try { p.Kill(true); } catch { } }  // the browser stops waiting after 45 s
        var json = read.Wait(5_000) ? read.Result : "";
        var c = p.HasExited && p.ExitCode == 0 ? SiteVideo.ParseChoices(json) : null;
        if (c == null) return new { ok = false, error = "The video could not be looked up" };
        return new { ok = true, title = c.Title, soundSize = c.SoundSize ?? 0,
                     qualities = c.Video.Take(12).Select(q => new { height = q.Height, size = q.Size ?? 0 }).ToList() };
    }
    catch (Exception e) { return new { ok = false, error = e.Message }; }
}

while (NativeMessaging.Read(input) is { } msg)
{
    var type = msg["type"]?.GetValue<string>();
    switch (type)
    {
        case "ping":
            NativeMessaging.Write(output, new { ok = true, version = Version });
            break;
        case "download":
            var job = new DownloadJob(
                msg["url"]?.GetValue<string>() ?? "",
                msg["fileName"]?.GetValue<string>(),
                msg["referrer"]?.GetValue<string>(),
                msg["cookies"]?.GetValue<string>(),
                msg["userAgent"]?.GetValue<string>()) { Start = msg["later"]?.GetValue<bool>() == true ? false : null, Stream = msg["stream"]?.GetValue<bool>() == true ? true : null,
                  Site = msg["site"]?.GetValue<bool>() == true ? true : null, AudioOnly = msg["audioOnly"]?.GetValue<bool>() == true ? true : null,
                  MaxHeight = msg["maxHeight"] is System.Text.Json.Nodes.JsonValue hv && hv.TryGetValue<double>(out var mh) && mh >= 100 && mh <= 10000 ? (int)mh : null,
                  Size = msg["size"] is System.Text.Json.Nodes.JsonValue sv && sv.TryGetValue<double>(out var sz) && sz > 0 ? (long)sz : null,
                  FromBrowser = msg["browser"]?.GetValue<bool>() == true ? true : null };
            if (!job.IsValid)
            {
                NativeMessaging.Write(output, new { ok = false, error = "Only http and https links can be downloaded" });
                break;
            }
            try
            {
                new Inbox().Drop(job);
                StartApp();
                NativeMessaging.Write(output, new { ok = true });
            }
            catch (Exception e)
            {
                NativeMessaging.Write(output, new { ok = false, error = e.Message });
            }
            break;
        case "qualities":
            NativeMessaging.Write(output, Qualities(msg));
            break;
        case "open":
            StartApp();
            NativeMessaging.Write(output, new { ok = true });
            break;
        default:
            NativeMessaging.Write(output, new { ok = false, error = "Unknown message" });
            break;
    }
}
return 0;
