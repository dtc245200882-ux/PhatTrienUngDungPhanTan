using Minio;
using Minio.DataModel.Args;
using MongoDB.Driver;
using Video.API.Models;
using Video.API.Services;
using Video.Contracts.Messages;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddSingleton<IMinioClient>(_ => new MinioClient()
    .WithEndpoint(minioEndpoint)
    .WithCredentials(minioAccessKey, minioSecretKey)
    .WithSSL(minioUseSsl)
    .Build());
builder.Services.AddSingleton<RabbitMqPublisher>();

var app = builder.Build();

var minioClient = app.Services.GetRequiredService<IMinioClient>();
var bucketExists = await minioClient.BucketExistsAsync(
    new BucketExistsArgs().WithBucket(bucketName));
if (!bucketExists)
{
    await minioClient.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucketName));
}

app.MapGet("/api/videos", async (
    IMongoCollection<VideoMetadata> videos,
    CancellationToken cancellationToken) =>
{
    var records = await videos.Find(Builders<VideoMetadata>.Filter.Empty)
        .SortByDescending(video => video.UploadedAt)
        .ToListAsync(cancellationToken);

    var response = records.Select(video => new VideoMetadataResponse(
        video.Id,
        video.FileName,
        video.Status,
        new DateTimeOffset(DateTime.SpecifyKind(video.UploadedAt, DateTimeKind.Utc))));

    return Results.Ok(response);
});

app.MapPost("/api/videos/upload", async (
    IFormFile file,
    IMongoCollection<VideoMetadata> videos,
    IMinioClient storage,
    RabbitMqPublisher publisher,
    CancellationToken cancellationToken) =>
{
    if (file.Length == 0)
    {
        return Results.BadRequest(new { error = "The uploaded file is empty." });
    }

    var videoId = Guid.NewGuid().ToString("D");
    var objectName = $"{videoId}{Path.GetExtension(file.FileName)}";

    await using (var stream = file.OpenReadStream())
    {
        await storage.PutObjectAsync(new PutObjectArgs()
            .WithBucket(bucketName)
            .WithObject(objectName)
            .WithStreamData(stream)
            .WithObjectSize(file.Length)
            .WithContentType(string.IsNullOrWhiteSpace(file.ContentType)
                ? "application/octet-stream"
                : file.ContentType), cancellationToken);
    }

    var metadata = new VideoMetadata
    {
        Id = videoId,
        FileName = Path.GetFileName(file.FileName),
        Status = "Pending",
        UploadedAt = DateTime.UtcNow
    };
    await videos.InsertOneAsync(metadata, cancellationToken: cancellationToken);

    await publisher.PublishAsync(
        new VideoUploadedMessage(videoId, metadata.FileName),
        cancellationToken);

    return Results.Accepted(value: new { videoId });
})
.DisableAntiforgery();

app.Run();