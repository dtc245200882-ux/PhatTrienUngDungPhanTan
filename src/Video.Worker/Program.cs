using Minio;
using MongoDB.Driver;
using RabbitMQ.Client;
using Video.Worker.Models;
using Video.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddSimpleConsole(options => options.IncludeScopes = true);

var mongoConnectionString = builder.Configuration["MongoDB:ConnectionString"]
    ?? throw new InvalidOperationException("MongoDB:ConnectionString is required.");
var mongoDatabaseName = builder.Configuration["MongoDB:DatabaseName"]
    ?? throw new InvalidOperationException("MongoDB:DatabaseName is required.");
var mongoCollectionName = builder.Configuration["MongoDB:CollectionName"]
    ?? throw new InvalidOperationException("MongoDB:CollectionName is required.");
var jobsCollectionName = builder.Configuration["MongoDB:JobsCollectionName"] ?? "Jobs";
var minioEndpoint = builder.Configuration["MinIO:Endpoint"]
    ?? throw new InvalidOperationException("MinIO:Endpoint is required.");
var minioAccessKey = builder.Configuration["MinIO:AccessKey"]
    ?? throw new InvalidOperationException("MinIO:AccessKey is required.");
var minioSecretKey = builder.Configuration["MinIO:SecretKey"]
    ?? throw new InvalidOperationException("MinIO:SecretKey is required.");
var minioUseSsl = builder.Configuration.GetValue<bool>("MinIO:UseSSL");
var rabbitMqUri = builder.Configuration["RabbitMQ:Uri"]
    ?? throw new InvalidOperationException("RabbitMQ:Uri is required.");

builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoConnectionString));
builder.Services.AddSingleton<IMongoCollection<VideoMetadata>>(services =>
    services.GetRequiredService<IMongoClient>()
        .GetDatabase(mongoDatabaseName)
        .GetCollection<VideoMetadata>(mongoCollectionName));
builder.Services.AddSingleton<IMongoCollection<JobMetadata>>(services =>
    services.GetRequiredService<IMongoClient>()
        .GetDatabase(mongoDatabaseName)
        .GetCollection<JobMetadata>(jobsCollectionName));
builder.Services.AddSingleton<IMinioClient>(_ => new MinioClient()
    .WithEndpoint(minioEndpoint)
    .WithCredentials(minioAccessKey, minioSecretKey)
    .WithSSL(minioUseSsl)
    .Build());

var rabbitFactory = new ConnectionFactory
{
    Uri = new Uri(rabbitMqUri),
    AutomaticRecoveryEnabled = true,
    NetworkRecoveryInterval = TimeSpan.FromSeconds(10)
};
var startupRetryAttempts = Math.Max(1, builder.Configuration.GetValue("RabbitMQ:StartupRetryAttempts", 10));
using var startupLoggerFactory = LoggerFactory.Create(logging => logging.AddSimpleConsole());
var startupLogger = startupLoggerFactory.CreateLogger("RabbitMQStartup");
IConnection rabbitConnection;
for (var attempt = 1; ; attempt++)
{
    try
    {
        rabbitConnection = await rabbitFactory.CreateConnectionAsync();
        break;
    }
    catch (Exception exception) when (attempt < startupRetryAttempts)
    {
        var delaySeconds = Math.Min(1 << Math.Min(attempt - 1, 5), 30);
        startupLogger.LogWarning(
            exception,
            "RabbitMQ connection attempt {Attempt}/{MaxAttempts} failed; retrying in {DelaySeconds}s.",
            attempt,
            startupRetryAttempts,
            delaySeconds);
        await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
    }
}

builder.Services.AddSingleton<IConnection>(rabbitConnection);
builder.Services.AddHostedService<VideoProcessingWorker>();
builder.Services.AddHostedService<WorkerHealthServer>();

await builder.Build().RunAsync();