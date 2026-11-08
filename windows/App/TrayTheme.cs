using System.Drawing;
using System.Windows.Forms;

namespace Nexonic.App;

/// <summary>Windows Forms tray menus are not WPF controls; render them with the active palette separately.</summary>
public static class TrayTheme
{
    public static void Apply(ContextMenuStrip menu)
    {
        var name = ThemeManager.CurrentTheme;
        var colors = name switch
        {
            "Dark" => ("#252930", "#E8EBF0", "#37414E", "#47515F"),
            "Slate" => ("#293B48", "#E7F0F5", "#35566B", "#486171"),
            "PaperGray" => ("#EAE9E7", "#232326", "#F2DABD", "#B3B0AB"),
            _ => ("#FFFFFF", "#20242A", "#EAF1FD", "#D0D7DE")
        };
        var back = ColorTranslator.FromHtml(colors.Item1);
        var fore = ColorTranslator.FromHtml(colors.Item2);
        var hover = ColorTranslator.FromHtml(colors.Item3);
        var border = ColorTranslator.FromHtml(colors.Item4);
        menu.RenderMode = ToolStripRenderMode.Professional;
        menu.Renderer = new PaletteRenderer(back, hover, border, fore);
        menu.ShowImageMargin = false;
        menu.BackColor = back;
        menu.ForeColor = fore;
        ApplyItems(menu.Items, back, fore);
    }

    static void ApplyItems(ToolStripItemCollection items, Color background, Color foreground)
    {
        foreach (ToolStripItem item in items)
        {
            item.BackColor = background;
            item.ForeColor = foreground;
            if (item is ToolStripDropDownItem parent)
            {
                parent.DropDown.BackColor = background;
                parent.DropDown.ForeColor = foreground;
                ApplyItems(parent.DropDownItems, background, foreground);
            }
        }
    }

    sealed class PaletteRenderer(Color background, Color hover, Color border, Color foreground)
        : ToolStripProfessionalRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(background);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(border);
            var rect = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            if (rect.Width > 0 && rect.Height > 0) e.Graphics.DrawRectangle(pen, rect);
        }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            using var brush = new SolidBrush(e.Item.Selected ? hover : background);
            e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(border);
            int y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 8, y, Math.Max(8, e.Item.Width - 8), y);
        }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = foreground;
            base.OnRenderArrow(e);
        }
    }
}
