namespace Prism.Mqtt;

/// <summary>Настройки MQTT-брокера. Пустой Address — MQTT отключён.</summary>
public sealed class BrokerOptions
{
    public string Address { get; set; } = "";
    public int Port { get; set; } = 1883;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
}
