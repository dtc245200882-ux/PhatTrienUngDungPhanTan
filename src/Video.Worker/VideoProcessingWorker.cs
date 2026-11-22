using System.Text.Json;
using Minio;
using Minio.DataModel.Args;
using MongoDB.Driver;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Video.Contracts.Messages;
using Video.Worker.Models;

namespace Video.Worker;

public sealed class VideoProcessingWorker : BackgroundService
{
    private readonly IConnection _rabbitConnection;
    private readonly IMongoCollection<VideoMetadata> _videos;
    private readonly IMinioClient _minio;
    private readonly string _queueName;
    private readonly string _bucketName;
    private readonly ILogger<VideoProcessingWorker> _logger;

    public VideoProcessingWorker(
        IConnection rabbitConnection,
        IMongoCollection<VideoMetadata> videos,
        IMinioClient minio,
        IConfiguration configuration,
        ILogger<VideoProcessingWorker> logger)
    {
        _rabbitConnection = rabbitConnection;
        _videos = videos;
        _minio = minio;
        _queueName = configuration["RabbitMQ:QueueName"]
            ?? throw new InvalidOperationException("RabbitMQ:QueueName is required.");
        _bucketName = configuration["MinIO:BucketName"]
            ?? throw new InvalidOperationException("MinIO:BucketName is required.");
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var channel = await _rabbitConnection.CreateChannelAsync(
            cancellationToken: stoppingToken);

        await channel.QueueDeclareAsync(
            queue: _queueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: stoppingToken);
        await channel.BasicQosAsync(0, 1, false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            VideoUploadedMessage? message = null;
            try
            {
                message = JsonSerializer.Deserialize<VideoUploadedMessage>(delivery.Body.Span)
                    ?? throw new InvalidOperationException("RabbitMQ message is empty.");

                await ProcessVideoAsync(message, stoppingToken);
                await channel.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);
                _logger.LogInformation("Processed video {VideoId}.", message.VideoId);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to process video message.");
                var requeue = false;

                if (message is not null)
                {
                    try
                    {
                        await UpdateStatusAsync(message.VideoId, "Failed", CancellationToken.None);
                    }
                    catch (Exception statusException)
                    {
                        _logger.LogError(statusException,
                            "Could not mark video {VideoId} as Failed; requeueing message.",
                            message.VideoId);
                        requeue = true;
                    }
                }

                await channel.BasicNackAsync(
                    delivery.DeliveryTag,
                    multiple: false,
                    requeue: requeue,
                    cancellationToken: CancellationToken.None);
            }
        };

        await channel.BasicConsumeAsync(
            queue: _queueName,
            autoAck: false,
            consumer: consumer,
            cancellationToken: stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessVideoAsync(
        VideoUploadedMessage message,
        CancellationToken cancellationToken)
    {
        var temporaryFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.tmp");
        try
        {
            await UpdateStatusAsync(message.VideoId, "Processing", cancellationToken);

            var objectName = $"{message.VideoId}{Path.GetExtension(message.FileName)}";
            await _minio.GetObjectAsync(new GetObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectName)
                .WithCallbackStream(stream =>
                {
                    using var output = File.Create(temporaryFile);
                    stream.CopyTo(output);
                }), cancellationToken);

            await Task.Delay(5000, cancellationToken);
            await UpdateStatusAsync(message.VideoId, "Completed", cancellationToken);
        }
        finally
        {
            if (File.Exists(temporaryFile))
            {
                File.Delete(temporaryFile);
            }
        }
    }

    private async Task UpdateStatusAsync(
        string videoId,
        string status,
        CancellationToken cancellationToken)
    {
        var filter = Builders<VideoMetadata>.Filter.Eq(video => video.Id, videoId);
        var update = Builders<VideoMetadata>.Update.Set(video => video.Status, status);
        var result = await _videos.UpdateOneAsync(
            filter,
            update,
            cancellationToken: cancellationToken);

        if (result.MatchedCount == 0)
        {
            throw new InvalidOperationException($"Video '{videoId}' was not found in MongoDB.");
        }
    }
}