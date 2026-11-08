using System.IO;
using System.IO.Pipes;
using System.Windows;
using Microsoft.Win32;
using Nexonic.Core;

namespace Nexonic.App;

public partial class App : Application
{
    const string PipeName = "nexonic-download-manager";
    Mutex? single;
    MainWindow? window;
    System.Windows.Forms.NotifyIcon? tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        // A .torrent file or magnet link the app was opened with goes to the running copy (or this one) to show.
        var torrents = TorrentInbox.Drop(e.Args);
        single = new Mutex(true, "Nexonic.DownloadManager.Single", out var first);
        if (!first)
        {
            // Another copy runs: wake it (it adds the new downloads and shows its window) and quit. Never open a second window.
            // One byte tells the running copy why it was woken: 'i' = new downloads from the browser, 's' = the user opened the app.
            var why = e.Args.Contains("--inbox") && torrents == 0 ? (byte)'i' : (byte)'s';
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try { using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly); c.Connect(1500); c.WriteByte(why); c.Flush(); break; }
                catch { Thread.Sleep(200); }
            }
            Shutdown();
            return;
        }
        Directory.CreateDirectory(AppPaths.Support);
        RegisterBrowserHost();
        var manager = new DownloadManager();
        ThemeManager.Apply(manager.Settings.Theme);
        L.Use(manager.Settings.Language);
        Translate.Install();
        window = new MainWindow(manager);
        // Started by the browser helper: stay in the tray unless the user wants the window for browser downloads.
        if (!e.Args.Contains("--inbox") || manager.Settings.BringToFrontOnBrowserDownload) window.Show();
        manager.DownloadDialogRequested += job => DownloadWindows.ShowInfo(manager, job);
        manager.TorrentRequested += source => { ShowWindow(); Dialogs.Torrent(window!, manager, source, DownloadQueue.MainId); };
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(manager.CollectTorrents));
        manager.ProgressRequested += row => DownloadWindows.ShowProgress(manager, row);
        manager.ShowRequested += ShowWindow;
        tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Text = "Meteor Download Manager",
            Visible = true,
        };
        tray.DoubleClick += (_, _) => ShowWindow();
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(L.T("Show Downloads"), null, (_, _) => ShowWindow());
        menu.Items.Add(L.T("Add URL…"), null, (_, _) => window!.ShowAdd(Dialogs.ClipboardLink()));
        menu.Items.Add(L.T("Stop All"), null, (_, _) => manager.PauseAll());
        menu.Items.Add(L.T("Resume All"), null, (_, _) => manager.ResumeAll());
        menu.Items.Add(SpeedLimitMenu(manager));
        menu.Items.Add(L.T("Quit"), null, (_, _) => Quit(manager));
        tray.ContextMenuStrip = menu;
        TrayTheme.Apply(menu);
        menu.Opening += (_, _) => TrayTheme.Apply(menu);
        ThemeManager.ThemeChanged += () => TrayTheme.Apply(menu);
        manager.Notify += (title, body) =>
        {
            if (manager.Settings.Notifications) tray.ShowBalloonTip(3000, title, body, System.Windows.Forms.ToolTipIcon.Info);
        };
        manager.Finished += r =>
        {
            if (manager.Settings.Notifications)
                tray.ShowBalloonTip(3000, "Download finished", r.FileName + (r.ChecksumResult is { } c ? " – " + c : ""), System.Windows.Forms.ToolTipIcon.Info);
            if (manager.Settings.ShowCompleteDialog) Dialogs.Complete(manager, r);
            if (manager.Settings.ExtractZips && Archives.IsZip(r.SavedPath)) Unzip(r.SavedPath!, text =>
            {
                if (manager.Settings.Notifications) tray.ShowBalloonTip(3000, "Unzipped", text, System.Windows.Forms.ToolTipIcon.Info);
            });
        };
        manager.ClipboardLink += link => window.ShowAdd(link);
        manager.QuitRequested += () => Quit(manager);
        manager.ApplyAutomation(f => Dispatcher.InvokeAsync(f).Task);
        Demo.Start(manager, window);
        Updater.Start(manager);
        _ = ListenAsync(manager);
    }

    /// <summary>Unzip when finished (Downloads menu): unpacks in the background, then reports on the UI thread.</summary>
    void Unzip(string path, Action<string> done)
    {
        var name = Path.GetFileName(path);
        Task.Run(() =>
        {
            string text;
            try { text = $"{name} → {Path.GetFileName(Archives.ExtractZip(path))}"; }
            catch (Exception e) { text = $"{name}: {e.Message}"; }
            Dispatcher.BeginInvoke(() => done(text));
        });
    }

    /// <summary>Tray quick setting: the speed limit for all downloads, from off to 10 MB/s.</summary>
    static System.Windows.Forms.ToolStripMenuItem SpeedLimitMenu(DownloadManager manager)
    {
        var item = new System.Windows.Forms.ToolStripMenuItem(L.T("Speed Limit"));
        foreach (var kb in SpeedLimits.Presets)
        {
            var choice = new System.Windows.Forms.ToolStripMenuItem(kb <= 0 ? L.T("No Limit") : SpeedLimits.Title(kb));
            choice.Click += (_, _) => { var s = manager.Settings; s.SpeedLimitKBps = kb; manager.ApplySettings(s); };
            item.DropDownItems.Add(choice);
        }
        item.DropDownOpening += (_, _) =>
        {
            var current = manager.Settings.SpeedLimitKBps;
            for (var i = 0; i < SpeedLimits.Presets.Length; i++)
                ((System.Windows.Forms.ToolStripMenuItem)item.DropDownItems[i]).Checked = SpeedLimits.Presets[i] == current;
        };
        return item;
    }

    void Quit(DownloadManager manager)
    {
        manager.Save();
        if (window != null) WindowPlace.Save(window);
        if (tray != null) tray.Visible = false;
        Shutdown();
    }

    void ShowWindow()
    {
        window!.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        window.Topmost = true;   // bring the existing window to the front, then let it behave normally again
        window.Topmost = false;
        window.Focus();
    }

    async Task ListenAsync(DownloadManager manager)
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync();
                var why = server.ReadByte();
                Dispatcher.Invoke(() =>
                {
                    manager.CollectInbox();
                    if (why != 'i') ShowWindow();
                });
            }
            catch { await Task.Delay(500); }
        }
    }

    /// <summary>Lets the browser extension find the helper (per user, no admin rights needed).</summary>
    static void RegisterBrowserHost()
    {
        var host = Path.Combine(AppContext.BaseDirectory, "nexonic-host.exe");
        if (!File.Exists(host)) return;
        var manifest = Path.Combine(AppPaths.Support, NativeMessaging.HostName + ".json");
        File.WriteAllText(manifest, NativeMessaging.Manifest(host));
        foreach (var browser in new[] { @"Google\Chrome", @"Microsoft\Edge", @"BraveSoftware\Brave-Browser", @"Chromium", @"Vivaldi" })
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\{browser}\NativeMessagingHosts\{NativeMessaging.HostName}");
            key.SetValue("", manifest);
        }
        var firefox = Path.Combine(AppPaths.Support, NativeMessaging.HostName + ".firefox.json");
        File.WriteAllText(firefox, NativeMessaging.FirefoxManifest(host));
        using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Mozilla\NativeMessagingHosts\{NativeMessaging.HostName}"))
            key.SetValue("", firefox);
    }
}
