using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Minio;
using Minio.DataModel.Args;
using MongoDB.Bson;
using MongoDB.Driver;
using RabbitMQ.Client;

namespace Video.Worker;

public sealed class WorkerHealthServer(
    IMongoClient mongoClient,
    IConnection rabbitConnection,
    IMinioClient minioClient,
    IConfiguration configuration,
    ILogger<WorkerHealthServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var healthPort = configuration.GetValue<int?>("Health:Port")
            ?? throw new InvalidOperationException("Health:Port is required.");
        var healthScheme = configuration["Health:Scheme"]
            ?? throw new InvalidOperationException("Health:Scheme is required.");
        var healthBindAddress = configuration["Health:BindAddress"]
            ?? throw new InvalidOperationException("Health:BindAddress is required.");
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"{healthScheme}://{healthBindAddress}:{healthPort}");
        var app = builder.Build();
        app.MapGet("/health", async (CancellationToken cancellationToken) =>
        {
            try
            {
                await mongoClient.GetDatabase("admin").RunCommandAsync<BsonDocument>(
                    new BsonDocument("ping", 1), cancellationToken: cancellationToken);
                var bucketName = configuration["MinIO:BucketName"]
                    ?? throw new InvalidOperationException("MinIO:BucketName is required.");
                var bucketExists = await minioClient.BucketExistsAsync(
                    new BucketExistsArgs().WithBucket(bucketName), cancellationToken);
                return rabbitConnection.IsOpen && bucketExists
                    ? Results.Ok(new { status = "healthy" })
                    : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Worker health check failed.");
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        });

        await app.RunAsync(stoppingToken);
    }
}