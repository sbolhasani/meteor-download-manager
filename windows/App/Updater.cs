using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Nexonic.Core;

namespace Nexonic.App;

/// <summary>Tells the user when a new version is published and installs it (Update Now, Remind Me Later, Skip This Version).</summary>
public static class Updater
{
    static DownloadManager? manager;
    static DispatcherTimer? daily;
    static bool busy;
    static bool installing;   // an update is downloading: never start a second one
    static Window? progressWindow;
    static bool closingProgress;

    /// <summary>Checks a few seconds after the app opens, then once a day while it keeps running.</summary>
    public static void Start(DownloadManager m)
    {
        manager = m;
        if (Environment.GetEnvironmentVariable("NDM_DEMO") == "1") return;   // not while taking screenshots
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        first.Tick += (_, _) => { first.Stop(); _ = Check(false); };
        first.Start();
        daily = new DispatcherTimer { Interval = TimeSpan.FromHours(24) };
        daily.Tick += (_, _) => _ = Check(false);
        daily.Start();
    }

    static HttpClient Client()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MeteorDownloadManager/" + HelpWindows.Version);
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static async Task Check(bool manual)
    {
        if (installing) { progressWindow?.Activate(); return; }   // already updating: show that instead
        if (manager is null || busy || (!manual && !manager.Settings.CheckForUpdates)) return;
        busy = true;
        Updates.Release? release = null;
        try
        {
            using var c = Client();
            c.Timeout = TimeSpan.FromSeconds(20);
            // GitHub's API first; when it refuses or fails, the copy of the same details attached to the release.
            foreach (var url in new[] { Updates.LatestUrl, Updates.MirrorUrl })
            {
                try { release = Updates.Parse(await c.GetStringAsync(url)); } catch { }
                if (release != null) break;
            }
        }
        catch { }
        finally { busy = false; }

        var current = HelpWindows.Version;
        if (installing) return;
        if (release is null)
        {
            if (manual) MessageBox.Show("GitHub didn't answer. Check your internet connection and try again later, or download the newest version from the website.", "Couldn't check for updates");
            return;
        }
        if (!Updates.IsNewer(release.Version, current))
        {
            if (manual) MessageBox.Show($"Meteor Download Manager {current} is the newest version.", "You're up to date");
            return;
        }
        if (!manual && manager.Settings.SkippedVersion == release.Version) return;

        var choice = Ask(release, current);
        if (installing) return;   // another check's Update Now got there first
        switch (choice)
        {
            case "update": await Install(release); break;
            case "skip":
                manager.Settings.SkippedVersion = release.Version;
                manager.ApplySettings(manager.Settings);
                break;
        }
    }

    static string Ask(Updates.Release r, string current)
    {
        var result = "later";
        var w = new Window
        {
            Title = "Update available", Width = 500, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true,
        };
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = "A new version of Meteor Download Manager is available", FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(new TextBlock { Text = $"Version {r.Version} is ready to install. You have {current}.", Margin = new Thickness(0, 6, 0, 10) });
        root.Children.Add(new TextBlock { Text = "What's new:", FontWeight = FontWeights.SemiBold });
        var notes = Updates.Notes(r.Notes, current);
        root.Children.Add(new TextBox
        {
            Text = notes.Length > 0 ? notes : "Improvements and fixes.", IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 190, Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(6),
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        void Add(string text, string value, bool isDefault = false, bool isCancel = false)
        {
            var b = new Button { Content = text, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(6, 0, 0, 0), IsDefault = isDefault, IsCancel = isCancel };
            b.Click += (_, _) => { result = value; w.Close(); };
            buttons.Children.Add(b);
        }
        Add("Skip This Version", "skip");
        Add("Remind Me Later", "later", isCancel: true);
        Add("Update Now", "update", isDefault: true);
        root.Children.Add(buttons);
        w.Content = root;
        w.ShowDialog();
        return result;
    }

    /// <summary>
    /// Installed copies download the new setup and run it quietly (it closes this copy, updates and opens the new one).
    /// The portable copy opens the release page instead.
    /// </summary>
    static async Task Install(Updates.Release r)
    {
        const string asset = "MeteorDownloadManager-Windows-Setup.exe";
        var installed = File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));
        // Only this app's own release file, and only when GitHub tells us its checksum; otherwise the release page.
        if (!installed || !r.Assets.TryGetValue(asset, out var url) || !Updates.TrustedAsset(url) || Updates.Digest(r, asset) is not { } digest)
        {
            HelpWindows.Open(r.Page);
            return;
        }
        var progress = new Window
        {
            Title = "Updating Meteor Download Manager", Width = 360, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Children =
                {
                    new TextBlock { Text = $"Downloading version {r.Version}…" },
                    new ProgressBar { IsIndeterminate = true, Height = 14, Margin = new Thickness(0, 10, 0, 0) },
                },
            },
        };
        installing = true;
        progressWindow = progress;
        progress.Closing += (_, e) => { if (installing && progress.IsVisible && !closingProgress) e.Cancel = true; };  // stays until the download ends
        progress.Show();
        var file = Path.Combine(Path.GetTempPath(), $"MeteorDownloadManager-{r.Version}-Setup.exe");
        try
        {
            using var c = Client();
            await using (var src = await c.GetStreamAsync(url))
            await using (var dst = File.Create(file))
                await src.CopyToAsync(dst);
            closingProgress = true; progress.Close(); closingProgress = false;
            if (!Updates.Verify(file, digest))
            {
                try { File.Delete(file); } catch { }
                installing = false;
                MessageBox.Show("The downloaded update did not match the published file, so it was not installed.\n" +
                                "Download it from the release page instead.", "Update");
                HelpWindows.Open(r.Page);
                return;
            }
            Process.Start(new ProcessStartInfo(file, "/SILENT /SP- /NOCANCEL /CLOSEAPPLICATIONS /NORESTART") { UseShellExecute = true });
        }
        catch (Exception e)
        {
            closingProgress = true; progress.Close(); closingProgress = false;
            installing = false;
            MessageBox.Show("The update could not be downloaded: " + e.Message + "\nTry again later, or download it from the release page.", "Update");
            return;
        }
        manager?.RequestQuit();
    }
}
