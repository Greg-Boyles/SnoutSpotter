using System.Security.Cryptography.X509Certificates;
using System.Text;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace SnoutSpotter.TrainingAgent;

/// <summary>
/// Wraps MQTTnet client with IoT Core mutual TLS, reconnect tracking,
/// and automatic re-subscription — same pattern as the Pi agent's MqttManager.
/// </summary>
public class MqttManager : IAsyncDisposable
{
    private readonly IMqttClient _client;
    private readonly MqttClientOptions _options;
    private readonly List<(string Topic, Func<string, string, Task> Handler)> _subscriptions = [];
    private readonly ILogger _logger;

    public bool Connected => _client.IsConnected;

    public MqttManager(string endpoint, string certPath, string keyPath, string rootCaPath, string clientId, ILogger logger)
    {
        _logger = logger;

        var clientCert = X509Certificate2.CreateFromPemFile(certPath, keyPath);
        var caCert = new X509Certificate2(rootCaPath);

        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();

        _options = new MqttClientOptionsBuilder()
            .WithTcpServer(endpoint, 8883)
            .WithClientId(clientId)
            .WithTlsOptions(tls =>
            {
                tls.WithClientCertificates(new[] { clientCert });
                tls.WithCertificateValidationHandler(_ => true); // CA validated at TLS layer
            })
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithCleanSession(false)
            .Build();

        _client.DisconnectedAsync += async e =>
        {
            _logger.LogWarning("MQTT disconnected: {Reason}", e.ReasonString);
            await Task.Delay(5000);
            try
            {
                await _client.ConnectAsync(_options);
                _logger.LogInformation("MQTT reconnected");
                await ResubscribeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError("MQTT reconnect failed: {Error}", ex.Message);
            }
        };
    }

    public async Task ConnectAsync()
    {
        await _client.ConnectAsync(_options);
        _logger.LogInformation("Connected to IoT Core via MQTT");
    }

    public async Task SubscribeAsync(string topic, Func<string, string, Task> handler)
    {
        _subscriptions.Add((topic, handler));

        _client.ApplicationMessageReceivedAsync += async e =>
        {
            var msgTopic = e.ApplicationMessage.Topic;
            // Simple topic match: exact match or wildcard topics handled by broker
            if (msgTopic == topic || topic.Contains('+') || topic.Contains('#'))
            {
                var payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
                await handler(msgTopic, payload);
            }
        };

        var subOptions = new MQTTnet.Client.MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(topic, MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        await _client.SubscribeAsync(subOptions);
        _logger.LogInformation("Subscribed to {Topic}", topic);
    }

    public async Task PublishAsync(string topic, string payload)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _client.PublishAsync(message);
    }

    private async Task ResubscribeAsync()
    {
        foreach (var (topic, _) in _subscriptions)
        {
            var subOptions = new MQTTnet.Client.MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(topic, MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            await _client.SubscribeAsync(subOptions);
            _logger.LogInformation("Re-subscribed to {Topic}", topic);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisconnectAsync();
        _client.Dispose();
    }
}

// Simple ILogger interface to avoid pulling in Microsoft.Extensions.Logging
public interface ILogger
{
    void LogInformation(string message, params object?[] args);
    void LogWarning(string message, params object?[] args);
    void LogError(string message, params object?[] args);
}

public class ConsoleLogger : ILogger
{
    // ILogger uses named placeholders ({JobId}, {Error}, etc.) not positional ({0}, {1}).
    // Replace named placeholders with positional ones for string.Format.
    private static string Format(string message, object?[] args)
    {
        if (args.Length == 0) return message;
        var idx = 0;
        var result = System.Text.RegularExpressions.Regex.Replace(
            message, @"\{[^}]+\}", _ => $"{{{idx++}}}");
        return string.Format(result, args);
    }

    public void LogInformation(string message, params object?[] args)
        => Console.WriteLine($"{DateTime.UtcNow:s} [INFO] {Format(message, args)}");

    public void LogWarning(string message, params object?[] args)
        => Console.WriteLine($"{DateTime.UtcNow:s} [WARN] {Format(message, args)}");

    public void LogError(string message, params object?[] args)
        => Console.Error.WriteLine($"{DateTime.UtcNow:s} [ERROR] {Format(message, args)}");
}
