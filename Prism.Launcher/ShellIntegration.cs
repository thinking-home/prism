using Microsoft.Win32;

namespace Prism.Launcher;

/// <summary>
/// Интеграция с оболочкой Windows без сторонних пакетов: пункт «Отправить» и
/// автозапуск в трее. Обе операции идемпотентны и работают в профиле текущего
/// пользователя (без прав администратора). Позже это же сделает инсталлятор.
/// </summary>
public static class ShellIntegration
{
    private const string SendToShortcut = "Prism.lnk";
    private const string RunValueName = "PrismLauncher";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    /// <summary>Создаёт ярлык в папке «Отправить», если его ещё нет.</summary>
    public static void EnsureSendToShortcut()
    {
        try
        {
            var sendTo = Environment.GetFolderPath(Environment.SpecialFolder.SendTo);
            var lnk = Path.Combine(sendTo, SendToShortcut);
            if (File.Exists(lnk)) return;
            CreateShortcut(lnk, ExePath, "Отправить видео на плеер Prism");
        }
        catch { /* не критично: пункт можно добавить и инсталлятором */ }
    }

    /// <summary>Прописывает автозапуск лаунчера при входе в систему.</summary>
    public static void EnsureAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.SetValue(RunValueName, $"\"{ExePath}\"");
        }
        catch { /* не критично */ }
    }

    private static void CreateShortcut(string lnkPath, string target, string description)
    {
        // WScript.Shell по COM — стандартный способ создать .lnk без библиотек.
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return;
        dynamic shell = Activator.CreateInstance(shellType)!;
        var shortcut = shell.CreateShortcut(lnkPath);
        shortcut.TargetPath = target;
        shortcut.Description = description;
        shortcut.WorkingDirectory = Path.GetDirectoryName(target) ?? "";
        shortcut.Save();
    }
}
