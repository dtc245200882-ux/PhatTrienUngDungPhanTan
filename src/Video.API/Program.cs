using Minio;
using Minio.DataModel.Args;
using Microsoft.AspNetCore.Http.Features;
using MongoDB.Bson;
using MongoDB.Driver;
using Video.API.Models;
using Video.API.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(options => options.IncludeScopes = true);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1_000_000_000);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 1_000_000_000);

var mongoConnectionString = builder.Configuration["MongoDB:ConnectionString"]
    ?? throw new InvalidOperationException("MongoDB:ConnectionString is required.");
var mongoDatabaseName = builder.Configuration["MongoDB:DatabaseName"]
    ?? throw new InvalidOperationException("MongoDB:DatabaseName is required.");
var mongoCollectionName = builder.Configuration["MongoDB:CollectionName"]
    ?? throw new InvalidOperationException("MongoDB:CollectionName is required.");
var minioEndpoint = builder.Configuration["MinIO:Endpoint"]
    ?? throw new InvalidOperationException("MinIO:Endpoint is required.");
var minioAccessKey = builder.Configuration["MinIO:AccessKey"]
    ?? throw new InvalidOperationException("MinIO:AccessKey is required.");
var minioSecretKey = builder.Configuration["MinIO:SecretKey"]
    ?? throw new InvalidOperationException("MinIO:SecretKey is required.");
var minioUseSsl = builder.Configuration.GetValue<bool>("MinIO:UseSSL");
var bucketName = builder.Configuration["MinIO:BucketName"]
    ?? throw new InvalidOperationException("MinIO:BucketName is required.");

builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoConnectionString));
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IMongoClient>()
        .GetDatabase(mongoDatabaseName)
        .GetCollection<VideoMetadata>(mongoCollectionName));
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IMongoClient>()
        .GetDatabase(mongoDatabaseName)
        .GetCollection<JobMetadata>(builder.Configuration["MongoDB:JobsCollectionName"] ?? "Jobs"));
builder.Services.AddSingleton<IMinioClient>(_ => new MinioClient()
    .WithEndpoint(minioEndpoint)
    .WithCredentials(minioAccessKey, minioSecretKey)
    .WithSSL(minioUseSsl)
    .Build());
builder.Services.AddSingleton<RabbitMqPublisher>();

var app = builder.Build();

app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 128)
    {
        correlationId = Guid.NewGuid().ToString("N");
    }

    context.Response.Headers["X-Correlation-ID"] = correlationId;
    using (app.Logger.BeginScope("CorrelationId:{CorrelationId}", correlationId))
    {
        await next();
    }
});

var minioClient = app.Services.GetRequiredService<IMinioClient>();
var bucketExists = await minioClient.BucketExistsAsync(
    new BucketExistsArgs().WithBucket(bucketName));
if (!bucketExists)
{
    await minioClient.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucketName));
}

VideoEndpoints.Map(app.MapGroup(""), bucketName);
VideoEndpoints.Map(app.MapGroup("/api"), bucketName);

app.MapGet("/health", async (IMongoClient mongoClient, CancellationToken cancellationToken) =>
{
    try
    {
        await mongoClient.GetDatabase("admin").RunCommandAsync<BsonDocument>(
            new BsonDocument("ping", 1), cancellationToken: cancellationToken);
        return Results.Ok(new { status = "healthy" });
    }
    catch
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();