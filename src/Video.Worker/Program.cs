using Minio;
using MongoDB.Driver;
using RabbitMQ.Client;
using Video.Worker.Models;
using Video.Worker;

var builder = Host.CreateApplicationBuilder(args);

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
var rabbitMqUri = builder.Configuration["RabbitMQ:Uri"]
    ?? throw new InvalidOperationException("RabbitMQ:Uri is required.");

builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoConnectionString));
builder.Services.AddSingleton<IMongoCollection<VideoMetadata>>(services =>
    services.GetRequiredService<IMongoClient>()
        .GetDatabase(mongoDatabaseName)
        .GetCollection<VideoMetadata>(mongoCollectionName));
builder.Services.AddSingleton<IMinioClient>(_ => new MinioClient()
    .WithEndpoint(minioEndpoint)
    .WithCredentials(minioAccessKey, minioSecretKey)
    .WithSSL(minioUseSsl)
    .Build());

var rabbitConnection = await new ConnectionFactory
{
    Uri = new Uri(rabbitMqUri)
}.CreateConnectionAsync();
builder.Services.AddSingleton<IConnection>(rabbitConnection);
builder.Services.AddHostedService<VideoProcessingWorker>();

await builder.Build().RunAsync();