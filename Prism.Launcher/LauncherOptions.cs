using Microsoft.Extensions.Configuration;

namespace Prism.Launcher;

/// <summary>
/// Настройки лаунчера — только для чтения, из <c>appsettings.json</c> рядом с exe.
/// Менять — правкой файла и перезапуском лаунчера. Редактора в приложении нет
/// намеренно (меньше кода, нечему ломаться).
/// </summary>
public sealed class LauncherOptions
{
    public HostOptions Host { get; set; } = new();
    public BrokerOptions Broker { get; set; } = new();

    public static LauncherOptions Load()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();
        return config.Get<LauncherOptions>() ?? new LauncherOptions();
    }
}

/// <summary>Адрес хоста Prism, по которому ходят и лаунчер, и приставка.</summary>
public sealed class HostOptions
{
    public string Address { get; set; } = "";
    public int Port { get; set; } = 8080;

    public string BaseUrl => $"http://{Address}:{Port}";
}

public sealed class BrokerOptions
{
    public string Address { get; set; } = "";
    public int Port { get; set; } = 1883;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
}
