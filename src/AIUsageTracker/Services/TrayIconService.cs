using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AIUsageTracker.Models;
using AIUsageTracker.ViewModels;

namespace AIUsageTracker.Services;

public sealed class TrayIconService : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly SettingsService _settingsService;
    private readonly MainViewModel _viewModel;
    private readonly Action _toggleFlyoutAction;
    private readonly Action _openSettingsAction;
    private readonly Action _exitApplicationAction;

    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly ToolStripMenuItem _showWidgetItem;
    private readonly ToolStripMenuItem _clickThroughItem;
    private readonly ToolStripMenuItem _alwaysOnTopItem;
    private readonly ToolStripMenuItem _compactModeItem;
    private readonly ToolStripMenuItem _invertModeItem;

    private IntPtr _currentIconHandle = IntPtr.Zero;

    public TrayIconService(
        SettingsService settingsService,
        MainViewModel viewModel,
        Action toggleFlyoutAction,
        Action openSettingsAction,
        Action exitApplicationAction)
    {
        _settingsService = settingsService;
        _viewModel = viewModel;
        _toggleFlyoutAction = toggleFlyoutAction;
        _openSettingsAction = openSettingsAction;
        _exitApplicationAction = exitApplicationAction;

        _contextMenu = new ContextMenuStrip();

        var openFlyoutItem = new ToolStripMenuItem("Abrir Painel da Barra de Tarefas", null, (_, _) => _toggleFlyoutAction())
        {
            Font = new Font(_contextMenu.Font, FontStyle.Bold)
        };

        _showWidgetItem = new ToolStripMenuItem("Mostrar Widget no Desktop (HUD)", null, (_, _) => _viewModel.ToggleShowDesktopWidget());
        _clickThroughItem = new ToolStripMenuItem("Modo Click-Through (Atravessar Cliques)", null, (_, _) => _viewModel.ToggleClickThrough());
        _alwaysOnTopItem = new ToolStripMenuItem("Sempre no Topo (Always on Top)", null, (_, _) => _viewModel.ToggleAlwaysOnTop());
        _compactModeItem = new ToolStripMenuItem("Modo Compacto", null, (_, _) => _viewModel.ToggleCompactMode());
        _invertModeItem = new ToolStripMenuItem("Exibir % Restante (em vez de % Usada)", null, (_, _) => _viewModel.ToggleDisplayMode());

        var refreshItem = new ToolStripMenuItem("Atualizar Cotas Agora", null, (_, _) => _viewModel.RefreshCommand.Execute(null));
        var settingsItem = new ToolStripMenuItem("Configurações...", null, (_, _) => _openSettingsAction());
        var exitItem = new ToolStripMenuItem("Sair", null, (_, _) => _exitApplicationAction());

        _contextMenu.Items.Add(openFlyoutItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(_showWidgetItem);
        _contextMenu.Items.Add(_clickThroughItem);
        _contextMenu.Items.Add(_alwaysOnTopItem);
        _contextMenu.Items.Add(_compactModeItem);
        _contextMenu.Items.Add(_invertModeItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(refreshItem);
        _contextMenu.Items.Add(settingsItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            Text = "AI Usage Tracker",
            ContextMenuStrip = _contextMenu
        };

        _notifyIcon.MouseClick += NotifyIcon_MouseClick;
        _viewModel.UiStateUpdated += (_, _) => SyncTrayVisuals();

        SyncTrayVisuals();
    }

    private void NotifyIcon_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _toggleFlyoutAction();
        }
    }

    private void SyncTrayVisuals()
    {
        AppSettings cfg = _settingsService.Current;
        _showWidgetItem.Checked = cfg.ShowDesktopWidget;
        _clickThroughItem.Checked = cfg.ClickThrough;
        _alwaysOnTopItem.Checked = cfg.AlwaysOnTop;
        _compactModeItem.Checked = cfg.CompactMode;
        _invertModeItem.Checked = cfg.ShowRemainingInsteadOfUsed;

        _notifyIcon.Text = _viewModel.TraySummaryToolTip;
        UpdateDynamicIcon();
    }

    private void UpdateDynamicIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            Color borderColor = _viewModel.OverallStatus switch
            {
                QuotaStatusLevel.Critical => Color.FromArgb(239, 68, 68),
                QuotaStatusLevel.Warning => Color.FromArgb(245, 158, 11),
                QuotaStatusLevel.Normal => Color.FromArgb(16, 185, 129),
                _ => Color.FromArgb(100, 116, 139)
            };

            using var bgBrush = new SolidBrush(Color.FromArgb(245, 15, 23, 42));
            using var borderPen = new Pen(borderColor, 2f);

            g.FillRectangle(bgBrush, 2, 2, 28, 28);
            g.DrawRectangle(borderPen, 2, 2, 28, 28);

            // Draw 3 mini horizontal bars inside the icon representing the top 3 active quotas
            var topBars = _viewModel.QuotaBars.Take(3).ToList();
            int[] yOffsets = { 7, 14, 21 };

            using var trackBrush = new SolidBrush(Color.FromArgb(255, 30, 41, 59));
            for (int i = 0; i < 3; i++)
            {
                int y = yOffsets[i];
                g.FillRectangle(trackBrush, 6, y, 20, 4);

                if (i < topBars.Count && topBars[i].IsAvailable)
                {
                    QuotaBarViewModel bar = topBars[i];
                    Color fillColor = bar.StatusLevel switch
                    {
                        QuotaStatusLevel.Critical => Color.FromArgb(248, 113, 113),
                        QuotaStatusLevel.Warning => Color.FromArgb(251, 191, 36),
                        _ => Color.FromArgb(52, 211, 153)
                    };

                    int fillWidth = Math.Clamp((int)Math.Round(bar.DisplayPercentValue / 100.0 * 20.0), 2, 20);
                    using var fillBrush = new SolidBrush(fillColor);
                    g.FillRectangle(fillBrush, 6, y, fillWidth, 4);
                }
            }
        }

        IntPtr hIcon = bmp.GetHicon();
        Icon newIcon = (Icon)Icon.FromHandle(hIcon).Clone();
        _notifyIcon.Icon = newIcon;

        if (_currentIconHandle != IntPtr.Zero)
        {
            DestroyIcon(_currentIconHandle);
        }
        _currentIconHandle = hIcon;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();

        if (_currentIconHandle != IntPtr.Zero)
        {
            DestroyIcon(_currentIconHandle);
            _currentIconHandle = IntPtr.Zero;
        }
    }
}
