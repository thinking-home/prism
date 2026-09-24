using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Prism.Mqtt;

namespace Prism.Launcher;

/// <summary>
/// Живёт всё время работы приложения: значок в трее, оркестрация «файл → плеер».
/// Файл приходит из пункта «Отправить» (через <see cref="Program"/>). Один плеер —
/// команда уходит сразу; несколько — меню выбора у курсора. Настройки — readonly
/// из appsettings.json (правка + перезапуск), редактора в приложении нет.
/// </summary>
public sealed class LauncherContext : ApplicationContext
{
    // Реестр сам переподключается циклом 5 с, поэтому опрос состояния чаще
    // раза в 2 с смысла не имеет.
    private static readonly TimeSpan BrokerWatchInterval = TimeSpan.FromSeconds(2);

    private readonly NotifyIcon _tray;
    private readonly Control _marshal = new();
    private readonly LauncherOptions _options;
    private readonly PlayerRegistry _mqtt;
    private readonly Icon _iconNormal;
    private readonly Icon _iconOffline;
    private readonly System.Windows.Forms.Timer _brokerWatch;
    private bool? _brokerConnected; // null — состояние ещё не показано

    public LauncherContext()
    {
        _marshal.CreateControl(); // хэндл для маршалинга в UI-поток (BeginInvoke)
        _options = LauncherOptions.Load();

        _iconNormal = LoadTrayIcon("app.ico");
        _iconOffline = LoadTrayIcon("app-offline.ico");

        _tray = new NotifyIcon
        {
            Icon = _iconOffline, // до подключения честнее показывать «нет брокера»
            Text = "Prism Launcher",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };

        ShellIntegration.EnsureSendToShortcut();
        ShellIntegration.EnsureAutoStart();

        _mqtt = new PlayerRegistry(_options.Broker);
        _mqtt.Start();

        // Событий о подключении реестр не даёт, а состояние нужно только для
        // значка — хватает опроса из UI-потока.
        _brokerWatch = new System.Windows.Forms.Timer { Interval = (int)BrokerWatchInterval.TotalMilliseconds };
        _brokerWatch.Tick += (_, _) => ApplyBrokerState();
        _brokerWatch.Start();
        ApplyBrokerState();
    }

    /// <summary>
    /// Приводит значок и подсказку в соответствие с состоянием подключения к
    /// брокеру: без него ни один плеер не виден и «Отправить» работать не будет,
    /// поэтому значок приглушается и получает янтарную пометку с минусом.
    /// </summary>
    private void ApplyBrokerState()
    {
        var connected = _mqtt.Configured && _mqtt.IsConnected;
        if (_brokerConnected == connected) return;
        _brokerConnected = connected;

        _tray.Icon = connected ? _iconNormal : _iconOffline;
        _tray.Text = TrayText(connected
            ? "Prism Launcher"
            : _mqtt.Configured
                ? $"Prism Launcher — no MQTT broker at {_options.Broker.Address}:{_options.Broker.Port}"
                : "Prism Launcher — MQTT broker is not configured");
    }

    // NotifyIcon.Text длиннее 63 символов не принимает (длинное имя брокера).
    private static string TrayText(string text) => text.Length <= 63 ? text : text[..63];

    /// <summary>Выполнить действие в UI-потоке (вызывается из пайп-сервера/фоновых задач).</summary>
    public void Post(Action action)
    {
        if (_marshal.IsHandleCreated) _marshal.BeginInvoke(action);
        else action();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open settings file…", null, (_, _) => OpenSettingsFile());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());
        return menu;
    }

    // Открыть appsettings.json в редакторе по умолчанию (менять настройки — здесь).
    private static void OpenSettingsFile()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { /* нет ассоциации/файла — не критично */ }
    }

    /// <summary>Обработать присланный файл (точка входа из пункта «Отправить»).</summary>
    public void HandleFile(string path) => Post(() => _ = HandleFileAsync(path));

    private async Task HandleFileAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(_options.Host.Address))
        {
            Notify("Prism is not configured", "Set the host address in appsettings.json and restart the launcher.", ToolTipIcon.Warning);
            return;
        }

        if (!File.Exists(path))
        {
            Notify("File not found", path, ToolTipIcon.Error);
            return;
        }

        var host = new HostClient(_options.Host.BaseUrl);
        ResolvedMedia? media;
        try
        {
            media = await host.ResolveAsync(path);
        }
        catch (Exception ex)
        {
            Notify("Prism host unavailable", ex.Message, ToolTipIcon.Error);
            return;
        }

        if (media is null)
        {
            var dirs = await host.GetMediaDirectoriesAsync();
            var hint = dirs.Count == 0
                ? "The Prism host does not serve this file."
                : $"The file must be inside a media folder: {string.Join("; ", dirs)}";
            Notify("Not in the Prism library", hint, ToolTipIcon.Warning);
            return;
        }

        if (!media.Playable || string.IsNullOrEmpty(media.StreamUrl))
        {
            Notify("Cannot play this file", media.Title, ToolTipIcon.Warning);
            return;
        }

        var url = host.AbsoluteStreamUrl(media.StreamUrl);
        var players = _mqtt.Snapshot();

        if (players.Count == 0)
        {
            Notify("No players found", "No Prism player is available on the network.", ToolTipIcon.Warning);
            return;
        }

        if (players.Count == 1)
        {
            await SendOpen(players[0], url, media.Title);
            return;
        }

        ShowPlayerMenu(players, url, media.Title);
    }

    private void ShowPlayerMenu(IReadOnlyList<PlayerSnapshot> players, string url, string title)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"Play “{title}” on:") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        foreach (var player in players)
        {
            var p = player;
            menu.Items.Add(p.Name, null, async (_, _) => await SendOpen(p, url, title));
        }
        menu.Show(Cursor.Position);
    }

    private async Task SendOpen(PlayerSnapshot player, string url, string title)
    {
        try
        {
            await _mqtt.OpenAsync(player.Id, url);
            Notify("Sent to player", $"“{title}” → {player.Name}", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Notify("Failed to send", ex.Message, ToolTipIcon.Error);
        }
    }

    private void Notify(string title, string text, ToolTipIcon icon) =>
        Post(() => _tray.ShowBalloonTip(4000, title, text, icon));

    /// <summary>Готовый значок нужного размера из встроенного ICO (иначе — копия системного).</summary>
    private static Icon LoadTrayIcon(string resourceName)
    {
        try
        {
            using var stream = typeof(LauncherContext).Assembly.GetManifestResourceStream(resourceName);
            if (stream is not null)
                return new Icon(stream, SystemInformation.SmallIconSize);
        }
        catch { /* не смогли — системный значок */ }
        return (Icon)SystemIcons.Application.Clone();
    }

    private void Exit()
    {
        _brokerWatch.Stop();
        _tray.Visible = false;
        _mqtt.Dispose();
        _tray.Dispose();
        _iconOffline.Dispose();
        _iconNormal.Dispose();
        ExitThread();
    }
}
