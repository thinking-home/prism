using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace Prism.Launcher;

/// <summary>Плеер, увиденный в MQTT (из retained <c>info</c>/<c>state</c>).</summary>
public sealed record PlayerInfo(string Id, string Name, DateTime LastSeenUtc);

/// <summary>
/// Связь лаунчера с MQTT: подписка на <c>prism/player/+/info|state</c> (retained —
/// список приезжает сразу при коннекте), ведение актуального списка плееров и
/// публикация команды <c>open</c>. Переподключение — простой цикл с паузой.
/// </summary>
public sealed class MqttBridge : IDisposable
{
    private readonly IMqttClient _client;
    private readonly MqttClientOptions? _options;
    private readonly Dictionary<string, PlayerInfo> _players = new();
    private readonly object _lock = new();
    private volatile bool _running;

    public MqttBridge(BrokerOptions broker)
    {
        _client = new MqttFactory().CreateMqttClient();

        if (!string.IsNullOrWhiteSpace(broker.Address))
        {
            var b = new MqttClientOptionsBuilder()
                .WithCleanSession()
                .WithTcpServer(broker.Address, broker.Port);
            if (!string.IsNullOrEmpty(broker.User))
                b = b.WithCredentials(broker.User, broker.Password);
            _options = b.Build();
        }

        _client.ApplicationMessageReceivedAsync += OnMessage;
        _client.ConnectedAsync += OnConnected;
    }

    /// <summary>Запускает подключение и держит его (переподключение при обрыве).</summary>
    public void Start()
    {
        if (_options is null || _running) return;
        _running = true;
        _ = Task.Run(ConnectLoopAsync);
    }

    private async Task ConnectLoopAsync()
    {
        while (_running)
        {
            try
            {
                if (!_client.IsConnected)
                    await _client.ConnectAsync(_options!, CancellationToken.None);
            }
            catch { /* брокер недоступен — повторим */ }

            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }

    private async Task OnConnected(MqttClientConnectedEventArgs _)
    {
        var sub = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic("prism/player/+/info"))
            .WithTopicFilter(f => f.WithTopic("prism/player/+/state"))
            .Build();
        try { await _client.SubscribeAsync(sub); } catch { /* переподпишемся на следующем коннекте */ }
    }

    private Task OnMessage(MqttApplicationMessageReceivedEventArgs e)
    {
        // Топик: prism/player/{id}/{info|state}
        var parts = e.ApplicationMessage.Topic.Split('/');
        if (parts.Length < 4) return Task.CompletedTask;
        var id = parts[2];
        var kind = parts[3];

        try
        {
            var payload = e.ApplicationMessage.ConvertPayloadToString() ?? "";
            string? name = null;
            if (payload.Length > 0)
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("name", out var n)) name = n.GetString();
            }

            lock (_lock)
            {
                _players.TryGetValue(id, out var prev);
                // info несёт имя; state обновляет только свежесть.
                var displayName = kind == "info" ? (name ?? prev?.Name ?? id) : (prev?.Name ?? id);
                _players[id] = new PlayerInfo(id, displayName, DateTime.UtcNow);
            }
        }
        catch { /* мусорный пейлоад — игнорируем */ }

        return Task.CompletedTask;
    }

    /// <summary>Текущий список плееров (по имени).</summary>
    public IReadOnlyList<PlayerInfo> Players
    {
        get { lock (_lock) return _players.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList(); }
    }

    public bool IsConnected => _client.IsConnected;

    /// <summary>Публикует команду open с абсолютным URL в топик команд плеера.</summary>
    public async Task OpenAsync(string playerId, string url)
    {
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic($"prism/player/{playerId}/cmd")
            .WithPayload(JsonSerializer.Serialize(new { action = "open", url }))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        await _client.PublishAsync(msg, CancellationToken.None);
    }

    public void Dispose()
    {
        _running = false;
        try { _client.Dispose(); } catch { }
    }
}
