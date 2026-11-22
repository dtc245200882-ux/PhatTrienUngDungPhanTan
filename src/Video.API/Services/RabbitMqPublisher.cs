using System.Text.Json;
using RabbitMQ.Client;

namespace Video.API.Services;

public sealed class RabbitMqPublisher : IAsyncDisposable
{
    private readonly ConnectionFactory _factory;
    private readonly string _queueName;
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public RabbitMqPublisher(IConfiguration configuration)
    {
        _factory = new ConnectionFactory
        {
            Uri = new Uri(configuration["RabbitMQ:Uri"]!)
        };
        _queueName = configuration["RabbitMQ:QueueName"]!;
    }

    public async Task PublishAsync<T>(T message, CancellationToken cancellationToken)
    {
        await _publishLock.WaitAsync(cancellationToken);
        try
        {
            if (_channel is null)
            {
                _connection = await _factory.CreateConnectionAsync(cancellationToken);
                _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);
                await _channel.QueueDeclareAsync(
                    queue: _queueName,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: null,
                    cancellationToken: cancellationToken);
            }

            var body = JsonSerializer.SerializeToUtf8Bytes(message);
            await _channel.BasicPublishAsync(
                exchange: string.Empty,
                routingKey: _queueName,
                mandatory: false,
                basicProperties: new BasicProperties { DeliveryMode = DeliveryModes.Persistent },
                body: body,
                cancellationToken: cancellationToken);
        }
        finally
        {
            _publishLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _publishLock.WaitAsync();
        try
        {
            if (_channel is not null)
            {
                await _channel.DisposeAsync();
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
            }
        }
        finally
        {
            _publishLock.Release();
            _publishLock.Dispose();
        }
    }
}