using System.Diagnostics;
using System.Text;
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
    private readonly IMongoCollection<JobMetadata> _jobs;
    private readonly IMinioClient _minio;
    private readonly string _queueName;
    private readonly string _bucketName;
    private readonly string _retryQueueName;
    private readonly string _deadLetterQueueName;
    private readonly int _maxRetries;
    private readonly int _retryDelayMilliseconds;
    private readonly ILogger<VideoProcessingWorker> _logger;

    public VideoProcessingWorker(
        IConnection rabbitConnection,
        IMongoCollection<VideoMetadata> videos,
        IMongoCollection<JobMetadata> jobs,
        IMinioClient minio,
        IConfiguration configuration,
        ILogger<VideoProcessingWorker> logger)
    {
        _rabbitConnection = rabbitConnection;
        _videos = videos;
        _jobs = jobs;
        _minio = minio;
        _queueName = configuration["RabbitMQ:QueueName"]
            ?? throw new InvalidOperationException("RabbitMQ:QueueName is required.");
        _bucketName = configuration["MinIO:BucketName"]
            ?? throw new InvalidOperationException("MinIO:BucketName is required.");
        _retryQueueName = $"{_queueName}.retry";
        _deadLetterQueueName = $"{_queueName}.dead";
        _maxRetries = Math.Max(0, configuration.GetValue("RabbitMQ:MaxRetries", 3));
        _retryDelayMilliseconds = Math.Max(1000, configuration.GetValue("RabbitMQ:RetryDelayMilliseconds", 5000));
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var channel = await _rabbitConnection.CreateChannelAsync(cancellationToken: stoppingToken);
        await channel.QueueDeclareAsync(_queueName, true, false, false, cancellationToken: stoppingToken);
        await channel.QueueDeclareAsync(_deadLetterQueueName, true, false, false, cancellationToken: stoppingToken);
        await channel.QueueDeclareAsync(
            _retryQueueName,
            true,
            false,
            false,
            arguments: new Dictionary<string, object?>
            {
                ["x-message-ttl"] = _retryDelayMilliseconds,
                ["x-dead-letter-exchange"] = string.Empty,
                ["x-dead-letter-routing-key"] = _queueName
            },
            cancellationToken: stoppingToken);
        await channel.BasicQosAsync(0, 1, false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            VideoJobMessage? message = null;
            var correlationId = delivery.BasicProperties.CorrelationId
                ?? ReadCorrelationId(delivery.BasicProperties.Headers)
                ?? "unknown";
            using var correlationScope = _logger.BeginScope("CorrelationId:{CorrelationId}", correlationId);

            try
            {
                message = JsonSerializer.Deserialize<VideoJobMessage>(delivery.Body.Span)
                    ?? throw new InvalidOperationException("RabbitMQ message is empty.");
                using var jobScope = _logger.BeginScope(
                    "CorrelationId:{CorrelationId} JobId:{JobId} VideoId:{VideoId}",
                    message.CorrelationId,
                    message.JobId,
                    message.VideoId);

                if (await TryClaimJobAsync(message, stoppingToken))
                {
                    await ProcessVideoAsync(message, stoppingToken);
                }

                await channel.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);
                _logger.LogInformation("Acknowledged video job {JobId}.", message.JobId);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to process video job {JobId}.", message?.JobId);
                var retryCount = ReadRetryCount(delivery.BasicProperties.Headers);
                var nextQueue = retryCount < _maxRetries ? _retryQueueName : _deadLetterQueueName;
                try
                {
                    if (message is not null)
                    {
                        var status = nextQueue == _deadLetterQueueName ? "Failed" : "Pending";
                        var progress = nextQueue == _deadLetterQueueName ? (int?)null : 0;
                        await UpdateJobAsync(message.JobId, status, progress, null, null, exception.Message, CancellationToken.None);
                        await UpdateVideoStatusAsync(message.VideoId, CancellationToken.None);
                    }

                    await PublishRetryAsync(channel, delivery, nextQueue, retryCount + 1, exception.Message);
                    await channel.BasicAckAsync(delivery.DeliveryTag, false, CancellationToken.None);
                    _logger.LogWarning("Routed message to {QueueName} after attempt {Attempt}.", nextQueue, retryCount + 1);
                }
                catch (Exception publishException)
                {
                    _logger.LogError(publishException, "Could not route failed message; requeueing it.");
                    await channel.BasicNackAsync(delivery.DeliveryTag, false, true, CancellationToken.None);
                }
            }
        };

        await channel.BasicConsumeAsync(_queueName, false, consumer, cancellationToken: stoppingToken);
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> TryClaimJobAsync(VideoJobMessage message, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var processingLeaseExpiredAt = now.AddMinutes(-5);
        var claim = await _jobs.UpdateOneAsync(
            job => job.Id == message.JobId &&
                (job.Status == "Pending" ||
                    (job.Status == "Processing" && job.UpdatedAt < processingLeaseExpiredAt)),
            Builders<JobMetadata>.Update
                .Set(job => job.Status, "Processing")
                .Set(job => job.Progress, 0)
                .Set(job => job.OutputPath, null)
                .Set(job => job.Error, null)
                .Set(job => job.UpdatedAt, now),
            cancellationToken: cancellationToken);
        if (claim.MatchedCount > 0)
        {
            await UpdateVideoStatusAsync(message.VideoId, cancellationToken);
            return true;
        }

        var existingJob = await _jobs.Find(job => job.Id == message.JobId)
            .FirstOrDefaultAsync(cancellationToken);
        if (existingJob is null)
        {
            throw new InvalidOperationException($"Job '{message.JobId}' was not found in MongoDB.");
        }

        _logger.LogInformation("Skipping duplicate delivery for job {JobId} in state {Status}.", message.JobId, existingJob.Status);
        return false;
    }

    private async Task ProcessVideoAsync(VideoJobMessage message, CancellationToken cancellationToken)
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var sourcePath = Path.Combine(temporaryDirectory, Path.GetFileName(message.FileName));
        var isThumbnail = string.Equals(message.JobType, "Thumbnail", StringComparison.OrdinalIgnoreCase);
        var artifactExtension = isThumbnail ? ".jpg" : ".json";
        var artifactPath = Path.Combine(temporaryDirectory, $"{message.JobId}{artifactExtension}");

        try
        {
            var objectName = $"{message.VideoId}{Path.GetExtension(message.FileName)}";
            await _minio.GetObjectAsync(new GetObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectName)
                .WithCallbackStream(stream =>
                {
                    using var output = File.Create(sourcePath);
                    stream.CopyTo(output);
                }), cancellationToken);
            await UpdateJobAsync(message.JobId, "Processing", 25, null, null, null, cancellationToken);

            var sourceInfo = new FileInfo(sourcePath);
            string result;
            if (isThumbnail)
            {
                await CreateThumbnailAsync(sourcePath, artifactPath, cancellationToken);
                result = "Thumbnail generated from the uploaded video.";
            }
            else if (string.Equals(message.JobType, "Metadata", StringComparison.OrdinalIgnoreCase))
            {
                var metadata = new
                {
                    fileName = message.FileName,
                    extension = Path.GetExtension(message.FileName),
                    sizeBytes = sourceInfo.Length,
                    inspectedAt = DateTime.UtcNow
                };
                await File.WriteAllTextAsync(artifactPath, JsonSerializer.Serialize(metadata), cancellationToken);
                result = "Video file metadata extracted.";
            }
            else if (string.Equals(message.JobType, "Transcode", StringComparison.OrdinalIgnoreCase))
            {
                var simulation = new
                {
                    sourceFile = message.FileName,
                    sourceSizeBytes = sourceInfo.Length,
                    status = "Simulated transcode; no codec conversion was performed.",
                    createdAt = DateTime.UtcNow
                };
                await File.WriteAllTextAsync(artifactPath, JsonSerializer.Serialize(simulation), cancellationToken);
                result = "Simulated transcode artifact created.";
            }
            else
            {
                throw new InvalidOperationException($"Unsupported job type '{message.JobType}'.");
            }

            await UpdateJobAsync(message.JobId, "Processing", 60, null, null, null, cancellationToken);
            var outputObject = $"processed/{message.VideoId}/{message.JobId}{artifactExtension}";
            await using (var artifactStream = File.OpenRead(artifactPath))
            {
                await _minio.PutObjectAsync(new PutObjectArgs()
                    .WithBucket(_bucketName)
                    .WithObject(outputObject)
                    .WithStreamData(artifactStream)
                    .WithObjectSize(artifactStream.Length)
                    .WithContentType(isThumbnail ? "image/jpeg" : "application/json"),
                    cancellationToken);
            }

            await UpdateJobAsync(message.JobId, "Completed", 100, outputObject, result, null, cancellationToken);
            await UpdateVideoStatusAsync(message.VideoId, cancellationToken);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, true);
            }
        }
    }

    private static async Task CreateThumbnailAsync(
        string sourcePath,
        string thumbnailPath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y", "-i", sourcePath,
            "-frames:v", "1", "-vf", "scale=320:-2", thumbnailPath
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start ffmpeg to generate the thumbnail.");
        }

        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
            throw;
        }

        var error = await errorTask;
        if (process.ExitCode != 0 || !File.Exists(thumbnailPath))
        {
            throw new InvalidOperationException($"ffmpeg could not generate a thumbnail: {error}");
        }
    }

    private async Task UpdateJobAsync(
        string jobId,
        string status,
        int? progress,
        string? outputPath,
        string? result,
        string? error,
        CancellationToken cancellationToken)
    {
        var update = Builders<JobMetadata>.Update
            .Set(job => job.Status, status)
            .Set(job => job.UpdatedAt, DateTime.UtcNow)
            .Set(job => job.OutputPath, outputPath)
            .Set(job => job.Result, result)
            .Set(job => job.Error, error);
        if (progress.HasValue)
        {
            update = update.Set(job => job.Progress, progress.Value);
        }

        var updateResult = await _jobs.UpdateOneAsync(
            job => job.Id == jobId,
            update,
            cancellationToken: cancellationToken);
        if (updateResult.MatchedCount == 0)
        {
            throw new InvalidOperationException($"Job '{jobId}' was not found in MongoDB.");
        }
    }

    private async Task UpdateVideoStatusAsync(string videoId, CancellationToken cancellationToken)
    {
        var jobStatuses = await _jobs.Find(job => job.VideoId == videoId)
            .Project(job => job.Status)
            .ToListAsync(cancellationToken);
        var videoStatus = jobStatuses.Contains("Processing")
            ? "Processing"
            : jobStatuses.Contains("Pending")
                ? "Pending"
                : jobStatuses.Contains("Failed")
                    ? "Failed"
                    : jobStatuses.Count > 0 ? "Completed" : "Uploaded";
        var result = await _videos.UpdateOneAsync(
            video => video.Id == videoId,
            Builders<VideoMetadata>.Update.Set(video => video.Status, videoStatus),
            cancellationToken: cancellationToken);
        if (result.MatchedCount == 0)
        {
            throw new InvalidOperationException($"Video '{videoId}' was not found in MongoDB.");
        }
    }

    private static async Task PublishRetryAsync(
        IChannel channel,
        BasicDeliverEventArgs delivery,
        string queueName,
        int retryCount,
        string error)
    {
        var headers = delivery.BasicProperties.Headers is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(delivery.BasicProperties.Headers);
        headers["x-retry-count"] = retryCount;
        headers["x-last-error"] = error;
        await channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: queueName,
            mandatory: true,
            basicProperties: new BasicProperties
            {
                DeliveryMode = DeliveryModes.Persistent,
                CorrelationId = delivery.BasicProperties.CorrelationId,
                Headers = headers
            },
            body: delivery.Body,
            cancellationToken: CancellationToken.None);
    }

    private static int ReadRetryCount(IDictionary<string, object?>? headers)
    {
        if (headers is null || !headers.TryGetValue("x-retry-count", out var value))
        {
            return 0;
        }

        return value switch
        {
            int count => count,
            long count => checked((int)count),
            byte[] bytes when int.TryParse(Encoding.UTF8.GetString(bytes), out var count) => count,
            _ => 0
        };
    }

    private static string? ReadCorrelationId(IDictionary<string, object?>? headers)
    {
        if (headers is null || !headers.TryGetValue("CorrelationId", out var value))
        {
            return null;
        }

        return value switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            ReadOnlyMemory<byte> bytes => Encoding.UTF8.GetString(bytes.Span),
            _ => value?.ToString()
        };
    }
}
