using System.Windows;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace MultiSound.App.Services;

/// <summary>Ícone na bandeja do Windows (área de notificação) com menu rápido.</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _toggleItem;
    private bool _balloonShown;

    public TrayIconService(Action open, Action togglePlayback, Action exit)
    {
        var menu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false,
            BackColor = Drawing.Color.FromArgb(0x1E, 0x20, 0x27),
            ForeColor = Drawing.Color.FromArgb(0xE8, 0xEA, 0xF0),
            Renderer = new Forms.ToolStripProfessionalRenderer(new DarkColorTable()),
        };
        menu.Items.Add("Abrir MultiSound", null, (_, _) => open());
        _toggleItem = new Forms.ToolStripMenuItem("Iniciar reprodução", null, (_, _) => togglePlayback());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => exit());

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "MultiSound",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) open(); };
    }

    public void Update(bool playing, string status)
    {
        _toggleItem.Text = playing ? "Parar reprodução" : "Iniciar reprodução";
        string text = $"MultiSound — {status}";
        _icon.Text = text.Length > 63 ? text[..63] : text; // limite do Windows
    }

    /// <summary>Avisa uma única vez que o app continua rodando na bandeja.</summary>
    public void ShowMinimizedHint()
    {
        if (_balloonShown) return;
        _balloonShown = true;
        _icon.ShowBalloonTip(3000, "MultiSound continua ativo",
            "O áudio continua sendo reproduzido. Clique no ícone para abrir.", Forms.ToolTipIcon.Info);
    }

    public void ShowNotice(string title, string text)
        => _icon.ShowBalloonTip(4000, title, text.Length > 250 ? text[..250] : text, Forms.ToolTipIcon.Warning);

    private static Drawing.Icon LoadIcon()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/multisound.ico"));
            if (info is not null)
            {
                using var s = info.Stream;
                return new Drawing.Icon(s, Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Tray icon", ex);
        }
        return Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }

    private sealed class DarkColorTable : Forms.ProfessionalColorTable
    {
        private static readonly Drawing.Color Bg = Drawing.Color.FromArgb(0x1E, 0x20, 0x27);
        private static readonly Drawing.Color Hover = Drawing.Color.FromArgb(0x2D, 0x31, 0x3C);
        public override Drawing.Color ToolStripDropDownBackground => Bg;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color MenuItemBorder => Hover;
        public override Drawing.Color MenuBorder => Drawing.Color.FromArgb(0x33, 0x36, 0x40);
        public override Drawing.Color SeparatorDark => Drawing.Color.FromArgb(0x33, 0x36, 0x40);
        public override Drawing.Color SeparatorLight => Bg;
        public override Drawing.Color ImageMarginGradientBegin => Bg;
        public override Drawing.Color ImageMarginGradientMiddle => Bg;
        public override Drawing.Color ImageMarginGradientEnd => Bg;
    }
}
