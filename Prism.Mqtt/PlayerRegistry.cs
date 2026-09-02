using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace Prism.Mqtt;

/// <summary>Плеер, увиденный в MQTT (снимок реестра).</summary>
public sealed record PlayerSnapshot(string Id, string Name,
    bool Online, double LastSeenSecondsAgo,
    string? Status, string? Url, double? PositionSec, double? DurationSec);

/// <summary>
/// Реестр плееров Prism в MQTT: подписка на retained <c>prism/player/+/info</c>
/// (имя) и <c>+/state</c> (статус/позиция, heartbeat ~5 с) — список приезжает
/// сразу при коннекте; публикация команды <c>open</c> в топик плеера. Общий код
/// библиотеки и лаунчера; подойдёт и умному дому, которому нужен список плееров.
///
/// Два режима владения клиентом:
/// - <see cref="PlayerRegistry(BrokerOptions, ILogger?)"/> — реестр сам создаёт
///   клиент, подключается и переподключается простым циклом с паузой;
/// - <see cref="PlayerRegistry(IMqttClient, ILogger?)"/> — клиент приходит
///   снаружи (например, у умного дома уже есть своё подключение): реестр только
///   вешает обработчики и подписывается, жизненный цикл клиента — на владельце
///   (реестр его не подключает и не освобождает).
///
/// online — эвристика по свежести сообщений (heartbeat 5 с → окно 15 с).
/// Нюанс retained: при (пере)коннекте state давно умершего плеера приезжает как
/// свежий, поэтому первые ~15 с после подключения такой плеер выглядит online;
/// собственного таймстемпа в пейлоаде state нет.
/// </summary>
public sealed class PlayerRegistry : IDisposable
{
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly IMqttClient _client;
    private readonly MqttClientOptions? _options;
    private readonly bool _ownsClient;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _players = new();
    private volatile bool _running;

    private sealed class Entry
    {
        public string Name = "";
        public string? Status;
        public string? Url;
        public double? PositionSec;
        public double? DurationSec;
        public DateTime LastSeenUtc;
    }

    /// <summary>Реестр со своим клиентом: сам подключается к брокеру из настроек
    /// и держит подключение. Пустой адрес — реестр отключён (Configured=false).</summary>
    public PlayerRegistry(BrokerOptions broker, ILogger? logger = null)
        : this(new MqttFactory().CreateMqttClient(), ownsClient: true, logger)
    {
        if (string.IsNullOrWhiteSpace(broker.Address)) return;

        var b = new MqttClientOptionsBuilder()
            .WithCleanSession()
            .WithTcpServer(broker.Address, broker.Port);
        if (!string.IsNullOrEmpty(broker.User))
            b = b.WithCredentials(broker.User, broker.Password);
        _options = b.Build();
    }

    /// <summary>Реестр поверх внешнего клиента: только обработчики и подписка,
    /// подключением и временем жизни клиента управляет владелец.</summary>
    public PlayerRegistry(IMqttClient client, ILogger? logger = null)
        : this(client, ownsClient: false, logger)
    {
    }

    private PlayerRegistry(IMqttClient client, bool ownsClient, ILogger? logger)
    {
        _client = client;
        _ownsClient = ownsClient;
        _logger = logger ?? NullLogger.Instance;
        _client.ApplicationMessageReceivedAsync += OnMessage;
        _client.ConnectedAsync += OnConnected;
    }

    /// <summary>Реестр в состоянии работать: свой клиент — задан адрес брокера;
    /// внешний клиент — всегда true.</summary>
    public bool Configured => !_ownsClient || _options is not null;

    public bool IsConnected => _client.IsConnected;

    /// <summary>Запускает реестр. Свой клиент — цикл подключения; внешний — только
    /// подписка, если владелец уже подключился (иначе подпишемся из его коннекта).</summary>
    public void Start()
    {
        if (_ownsClient)
        {
            if (_options is null || _running) return;
            _running = true;
            _ = Task.Run(ConnectLoopAsync);
        }
        else if (_client.IsConnected)
        {
            _ = Task.Run(SubscribeAsync);
        }
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
            catch (Exception ex)
            {
                _logger.LogDebug("MQTT-брокер недоступен: {message}", ex.Message);
            }

            await Task.Delay(ReconnectDelay);
        }
    }

    private Task OnConnected(MqttClientConnectedEventArgs _) => SubscribeAsync();

    private async Task SubscribeAsync()
    {
        var sub = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic("prism/player/+/info"))
            .WithTopicFilter(f => f.WithTopic("prism/player/+/state"))
            .Build();
        try
        {
            await _client.SubscribeAsync(sub);
            _logger.LogInformation("MQTT подключён, подписка на плееры оформлена");
        }
        catch { /* переподпишемся на следующем коннекте */ }
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
            if (payload.Length == 0) return Task.CompletedTask;
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            lock (_lock)
            {
                if (!_players.TryGetValue(id, out var entry))
                    _players[id] = entry = new Entry { Name = id };

                if (kind == "info")
                {
                    if (root.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name)
                        entry.Name = name;
                }
                else if (kind == "state")
                {
                    entry.Status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
                    entry.Url = root.TryGetProperty("url", out var u) ? u.GetString() : null;
                    entry.PositionSec = root.TryGetProperty("positionSec", out var p) && p.TryGetDouble(out var pos)
                        ? pos : null;
                    entry.DurationSec = root.TryGetProperty("durationSec", out var d) && d.TryGetDouble(out var dur)
                        ? dur : null;
                }
                entry.LastSeenUtc = DateTime.UtcNow;
            }
        }
        catch { /* мусорный пейлоад — игнорируем */ }

        return Task.CompletedTask;
    }

    /// <summary>Текущий список плееров (по имени).</summary>
    public IReadOnlyList<PlayerSnapshot> Snapshot()
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            return _players
                .Select(kv =>
                {
                    var age = (now - kv.Value.LastSeenUtc).TotalSeconds;
                    return new PlayerSnapshot(kv.Key, kv.Value.Name,
                        Online: age < OnlineWindow.TotalSeconds, Math.Round(age, 1),
                        kv.Value.Status, kv.Value.Url, kv.Value.PositionSec, kv.Value.DurationSec);
                })
                .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    public bool Knows(string playerId)
    {
        lock (_lock) return _players.ContainsKey(playerId);
    }

    /// <summary>Публикует команду open с абсолютным URL в топик команд плеера.</summary>
    public async Task OpenAsync(string playerId, string url, CancellationToken ct = default)
    {
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic($"prism/player/{playerId}/cmd")
            .WithPayload(JsonSerializer.Serialize(new { action = "open", url }))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        await _client.PublishAsync(msg, ct);
    }

    public void Dispose()
    {
        _running = false;
        _client.ApplicationMessageReceivedAsync -= OnMessage;
        _client.ConnectedAsync -= OnConnected;
        if (_ownsClient)
        {
            try { _client.Dispose(); } catch { /* уже освобождён */ }
        }
    }
}
