using System.Windows;
using Forms = System.Windows.Forms;

namespace WindowsIntelligence;

/// <summary>
/// Assistant-only host. The Copilot key, Siri-style edge effect, voice conversation and screenshot
/// Q&amp;A are the product; the Dynamic Island, media centre, workbench and reminders are gone.
/// </summary>
internal sealed partial class AppHost
{
    private Application app = null!;
    private Settings settings = null!;
    private Overlay overlay = null!;
    private Caption caption = null!;
    private Interaction interaction = null!;
    private SettingsWindow? window;
    private Forms.NotifyIcon? tray;

    partial void Initialize(Application application)
    {
        app = application;
        settings = Settings.Load();
        try { shortcut.Apply(settings.Shortcut); }
        catch (UserError) { } // An unavailable shortcut leaves the Copilot key active.
        overlay = new Overlay(settings);
        caption = new Caption();
        interaction = new Interaction(settings, app.Dispatcher, overlay, caption);
        if (!settings.Tray) return;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("开始语音", null, (_, _) => interaction.Start(false));
        menu.Items.Add("输入问题", null, (_, _) => interaction.Start(false, typed: true));
        menu.Items.Add("截图提问", null, (_, _) => interaction.Start(true));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("唤醒快捷键", null, (_, _) => { ShowSettings(); window?.ShowShortcutSettings(); });
        menu.Items.Add("设置", null, (_, _) => ShowSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出 Windows Intelligence", null, (_, _) => app.Shutdown());
        tray = new Forms.NotifyIcon
        {
            Visible = true,
            Text = "Windows Intelligence",
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
        };
        tray.DoubleClick += (_, _) => ShowSettings();
    }

    private void ShowSettings()
    {
        if (window is null)
        {
            window = new SettingsWindow(settings, interaction, overlay, fastPath, shortcut);
            window.Closed += (_, _) => window = null;
        }
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    partial void HandleActivation(Activation input)
    {
        switch (input)
        {
            case Activation.Open: ShowSettings(); break;
            case Activation.Background: break;
            // Tap starts the Siri-style listening session; holding starts the screenshot path.
            case Activation.Tap: interaction.Start(false, typed: false, canAutoSend: true); break;
            case Activation.SilentTap: interaction.Start(false, typed: true, silent: true); break;
            case Activation.Down: interaction.Start(true, held: true, typed: false, canAutoSend: true); break;
            case Activation.Up: interaction.Release(); break;
        }
    }

    partial void Shutdown()
    {
        window?.Close();
        tray?.Dispose();
        interaction.Dispose();
        caption.Dispose();
        overlay.Dispose();
    }
}
