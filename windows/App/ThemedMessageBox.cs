using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Nexonic.App;

/// <summary>Reusable themed replacement for standard WPF MessageBox, with an optional unchecked checkbox.</summary>
public static class ThemedMessageBox
{
    public sealed record Result(MessageBoxResult Choice, bool Checked);

    public static MessageBoxResult Show(string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None) =>
        ShowWithOption(null, message, title, buttons, image, defaultResult).Choice;

    public static MessageBoxResult Show(Window owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None) =>
        ShowWithOption(owner, message, title, buttons, image, defaultResult).Choice;

    public static Result ShowWithOption(Window? owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OKCancel, MessageBoxImage image = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None, string? optionText = null)
    {
        var cancelResult = buttons switch
        {
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.OK => MessageBoxResult.OK,
            _ => MessageBoxResult.Cancel,
        };
        if (defaultResult == MessageBoxResult.None) defaultResult = cancelResult;
        var choice = cancelResult;
        var dialog = new Window
        {
            Title = title, Width = 470, MinWidth = 350, MaxWidth = 640,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner?.IsVisible == true
                ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ShowInTaskbar = owner?.IsVisible != true,
            Topmost = owner?.IsVisible != true,
        };
        if (owner?.IsVisible == true) dialog.Owner = owner;
        var root = new StackPanel { Margin = new Thickness(20) };
        var row = new DockPanel { LastChildFill = true };
        if (image != MessageBoxImage.None)
        {
            var icon = new TextBlock
            {
                Text = image switch
                {
                    MessageBoxImage.Error => "×",
                    MessageBoxImage.Warning => "!",
                    MessageBoxImage.Question => "?",
                    _ => "i",
                },
                Width = 34, FontSize = 21, FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
            };
            icon.SetResourceReference(TextBlock.ForegroundProperty,
                image is MessageBoxImage.Warning or MessageBoxImage.Error ? "ErrorBrush" : "AccentBrush");
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
        }
        var messageText = new TextBlock
        {
            Text = message, TextWrapping = TextWrapping.Wrap,
            FontSize = 13, MaxWidth = 540,
        };
        messageText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        row.Children.Add(messageText);
        root.Children.Add(row);
        CheckBox? option = null;
        if (!string.IsNullOrWhiteSpace(optionText))
        {
            option = new CheckBox
            {
                Content = optionText, IsChecked = false,
                Margin = new Thickness(0, 16, 0, 0),
            };
            option.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            root.Children.Add(option);
        }
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };
        void Add(string caption, MessageBoxResult result)
        {
            var b = new Button
            {
                Content = caption, MinWidth = 90, Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(8, 0, 0, 0), IsDefault = result == defaultResult,
                IsCancel = result is MessageBoxResult.Cancel or MessageBoxResult.No,
            };
            b.Click += (_, _) => { choice = result; dialog.Close(); };
            actions.Children.Add(b);
        }
        switch (buttons)
        {
            case MessageBoxButton.OK:
                Add("OK", MessageBoxResult.OK); break;
            case MessageBoxButton.OKCancel:
                Add("Cancel", MessageBoxResult.Cancel);
                Add("OK", MessageBoxResult.OK); break;
            case MessageBoxButton.YesNo:
                Add("No", MessageBoxResult.No);
                Add("Yes", MessageBoxResult.Yes); break;
            case MessageBoxButton.YesNoCancel:
                Add("Cancel", MessageBoxResult.Cancel);
                Add("No", MessageBoxResult.No);
                Add("Yes", MessageBoxResult.Yes); break;
        }
        root.Children.Add(actions);
        dialog.Content = root;
        dialog.ShowDialog();
        return new Result(choice, choice is MessageBoxResult.OK or MessageBoxResult.Yes && option?.IsChecked == true);
    }
}
