using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Nexonic.App;

/// <summary>Help and About windows, with the same topics and links as the Mac app.</summary>
public static class HelpWindows
{
    public const string Author = "Shahrukh Khalid";
    public const string Website = "https://www.shahrukhalid.com";
    public const string GitHub = "https://github.com/Shahrukhalid";
    public const string Project = "https://github.com/Shahrukhalid/nexonic-download-manager";
    public const string OnlineHelp = Project + "/blob/main/docs/HELP.md";
    public const string Releases = Project + "/releases/latest";
    public const string ReportProblem = Project + "/issues/new";
    public const string License = Project + "/blob/main/LICENSE";
    public const string Terms = Project + "/blob/main/TERMS.md";

    public static string Version
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version;
            return v is null ? "dev" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    static readonly (string Title, (string Heading, string Text)[] Sections)[] Topics =
    {
        ("Getting Started", new[]
        {
            ("Add a download", "Click Add URL (Ctrl+N), paste one or more links and press Start Download. Links you copy are offered automatically when the Add window opens."),
            ("Pause and resume", "Select a download and press Stop or Resume. Downloads continue where they stopped, even after you close the app or restart the computer."),
            ("Find your files", "Finished files are saved in the folder chosen in the Add window (your Downloads folder by default). Double-click a download to open it, or right-click and choose Show in Folder."),
        }),
        ("Browser Extension", new[]
        {
            ("Chrome, Edge, Brave and Opera", "Open the app once. Unzip NexonicExtension-Chrome.zip (or use the Extension folder in the app's folder), open the browser's extensions page, turn on Developer mode, click Load unpacked and choose the folder."),
            ("Firefox", "Drag NexonicAddon-Firefox.xpi onto Firefox. For a quick test, open about:debugging and choose Load Temporary Add-on."),
            ("Download and progress windows", "When you click a download link in the browser, the download window shows the file name, size, category and folder: choose Start Download, Download Later or Cancel. Each download you start gets its own progress window with the speed, time left, a speed graph and every connection. Turn either window off in Options, General."),
            ("What it does", "Downloads from the browser are handed to the app automatically. Right-click a link and choose Download with Nexonic, or use the video button that appears on pages with video."),
        }),
        ("Saving Videos", new[]
        {
            ("From a website", "Click the video button the extension shows on the page, or paste the page link into Add URL. Videos are saved in the best available quality as .mp4."),
            ("Quality", "The download window and the Download this video button list the qualities the video is offered in. Pick one, or Sound only to save just the audio."),
            ("Streamed video", "Streams (.m3u8) are found by the extension, downloaded in parallel and joined into one .mp4. Copy-protected services are not supported."),
        }),
        ("Queues and Scheduler", new[]
        {
            ("Queues", "Put downloads in a queue to run them one after another or a few at a time. Right-click a download and choose Move to Queue."),
            ("Scheduler", "Start a queue at a set time, stop it at another, and optionally close the app, sleep or shut down the computer when it is done."),
        }),
        ("Batch and Site Grabber", new[]
        {
            ("Batch download", "Download a numbered series such as file01.jpg to file50.jpg by writing the link once with a pattern, for example file[01-50].jpg."),
            ("Site Grabber", "Enter a page address to list every file linked from it, filter by type and download the ones you check."),
        }),
        ("Speed and Options", new[]
        {
            ("Faster downloads", "Each file is split into parts that download at the same time. Change the number of connections in Options (up to 32)."),
            ("Speed limit", "Set a limit for all downloads in Options, or for one download in its Properties."),
            ("Proxy and sign-in", "Set a proxy in Options, Connection. For password-protected downloads, add the user name and password in the Add window or under Properties."),
        }),
        ("Command Line", new[]
        {
            ("Use ndm", "Open a new terminal and type ndm help. Example: ndm https://example.com/file.zip --queue Night"),
        }),
        ("Troubleshooting", new[]
        {
            ("A link has expired", "Right-click the download, choose Refresh Link and paste a fresh link. The download continues without starting over."),
            ("\"Windows protected your PC\"", "Click More info, then Run anyway. This appears because the installer is not signed with a paid certificate."),
            ("The browser doesn't hand over downloads", "Open the app once so it can register itself with your browsers, then restart the browser."),
            ("A video site stopped working", "The video engine updates itself every day. Try again later, and if it still fails, report the problem with the page link."),
            ("Updating the app", "When a new version is released, the app tells you when it opens, with what's new. Choose Update Now to install it (the app closes, updates and opens again), Remind Me Later, or Skip This Version. You can also use Check for Updates in the menu, or turn the check off in Options. The portable copy opens the download page instead."),
        }),
        ("Keyboard Shortcuts", new[]
        {
            ("Downloads", "Ctrl+N  Add URL\nCtrl+F  Search downloads\nDelete  Remove the selected downloads\nF1  Help"),
        }),
    };

    static Hyperlink Link(string text, string url)
    {
        var h = new Hyperlink(new Run(text)) { NavigateUri = new Uri(url) };
        h.RequestNavigate += (_, e) => { Open(e.Uri.ToString()); e.Handled = true; };
        return h;
    }

    static TextBlock LinkRow(params (string Text, string Url)[] links)
    {
        var t = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        for (var i = 0; i < links.Length; i++)
        {
            if (i > 0) t.Inlines.Add(new Run("   ·   ") { Foreground = Brushes.Gray });
            t.Inlines.Add(Link(links[i].Text, links[i].Url));
        }
        return t;
    }

    public static void ShowHelp(Window owner)
    {
        var list = new ListBox { Width = 210, BorderThickness = new Thickness(0, 0, 1, 0), BorderBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xE8, 0xEF)), Padding = new Thickness(4, 8, 4, 8) };
        foreach (var t in Topics) list.Items.Add(new ListBoxItem { Content = t.Title, Padding = new Thickness(8, 6, 8, 6) });
        var detail = new StackPanel { Margin = new Thickness(28, 20, 28, 20), MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left };

        void Show(int index)
        {
            detail.Children.Clear();
            var topic = Topics[index];
            detail.Children.Add(new TextBlock { Text = topic.Title, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
            foreach (var (heading, text) in topic.Sections)
            {
                detail.Children.Add(new TextBlock { Text = heading, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 3) });
                detail.Children.Add(new TextBox { Text = text, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Padding = new Thickness(0) });
            }
            detail.Children.Add(new Separator { Margin = new Thickness(0, 16, 0, 8) });
            var links = new TextBlock();
            links.Inlines.Add(Link("Online Help", OnlineHelp));
            links.Inlines.Add(new Run("     "));
            links.Inlines.Add(Link("Report a Problem", ReportProblem));
            links.Inlines.Add(new Run("     "));
            links.Inlines.Add(Link("Check for Updates", Releases));
            detail.Children.Add(links);
        }

        list.SelectionChanged += (_, _) => { if (list.SelectedIndex >= 0) Show(list.SelectedIndex); };
        var root = new DockPanel();
        DockPanel.SetDock(list, Dock.Left);
        root.Children.Add(list);
        root.Children.Add(new ScrollViewer { Content = detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var w = new Window
        {
            Title = "Meteor Download Manager Help", Owner = owner, Width = 860, Height = 560, MinWidth = 640, MinHeight = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = root, Icon = owner.Icon,
        };
        list.SelectedIndex = 0;
        w.Show();
    }

    public static void ShowAbout(Window owner)
    {
        var p = new StackPanel { Margin = new Thickness(28, 24, 28, 22) };
        // The window icon is the .ico's first (16 px) picture, which looks blurry at 80 px: use its largest one.
        ImageSource? icon = owner.Icon;
        try
        {
            var decoder = new System.Windows.Media.Imaging.IconBitmapDecoder(new Uri("pack://application:,,,/app.ico", UriKind.Absolute),
                System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            icon = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
        }
        catch { }
        if (icon != null)
        {
            var image = new Image { Source = icon, Width = 80, Height = 80, Margin = new Thickness(0, 0, 0, 10) };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            p.Children.Add(image);
        }
        p.Children.Add(new TextBlock { Text = "Meteor Download Manager", FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        p.Children.Add(new TextBlock { Text = $"Version {Version}", Foreground = Brushes.DimGray, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
        p.Children.Add(new TextBlock { Text = "Free, open-source download manager for Mac, Windows, Linux, Android, iPhone and iPad.", Foreground = Brushes.DimGray, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
        var made = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        made.Inlines.Add(new Run("Made by "));
        made.Inlines.Add(Link(Author, Website));
        p.Children.Add(made);
        p.Children.Add(LinkRow(("Website", Website), ("GitHub", GitHub), ("Source Code", Project), ("Report a Problem", ReportProblem)));
        p.Children.Add(new Separator { Margin = new Thickness(0, 14, 0, 10) });
        var legal = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brushes.Gray, FontSize = 11 };
        legal.Inlines.Add(new Run($"© 2026 {Author}.  "));
        legal.Inlines.Add(Link("License: MIT or Apache-2.0", License));
        legal.Inlines.Add(new Run("  ·  "));
        legal.Inlines.Add(Link("Terms of Use", Terms));
        p.Children.Add(legal);
        var w = new Window
        {
            Title = "About Meteor Download Manager", Owner = owner, Width = 460, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Icon = owner.Icon,
        };
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
        var update = new Button { Content = "Check for Updates", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(4, 0, 4, 0) };
        update.Click += (_, _) => _ = Updater.Check(true);
        var close = new Button { Content = "Close", MinWidth = 80, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(4, 0, 4, 0), IsDefault = true, IsCancel = true };
        close.Click += (_, _) => w.Close();
        buttons.Children.Add(update);
        buttons.Children.Add(close);
        p.Children.Add(buttons);
        w.Content = p;
        w.ShowDialog();
    }
}
