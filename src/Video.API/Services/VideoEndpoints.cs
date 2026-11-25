using Minio;
using Minio.DataModel.Args;
using MongoDB.Driver;
using System.Text.Json;
using Video.API.Models;
using Video.Contracts.Messages;

namespace Video.API.Services;

public static class VideoEndpoints
{
    public static void Map(RouteGroupBuilder routes, string bucketName)
    {
        routes.MapPost("/videos", async (
            IFormFile file,
            IMongoCollection<VideoMetadata> videos,
            IMinioClient storage,
            CancellationToken cancellationToken) =>
        {
            if (file.Length == 0)
            {
                return Results.BadRequest(new { error = "The uploaded file is empty." });
            }

            var videoId = Guid.NewGuid().ToString("D");
            var objectName = $"{videoId}{Path.GetExtension(Path.GetFileName(file.FileName))}";
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
                Status = "Uploaded",
                UploadedAt = DateTime.UtcNow
            };
            await videos.InsertOneAsync(metadata, cancellationToken: cancellationToken);
            return Results.Created($"/videos/{videoId}", new { videoId, metadata.FileName, metadata.Status });
        }).DisableAntiforgery();

        routes.MapPost("/videos/{id}/jobs", async (
            string id,
            HttpContext context,
            IMongoCollection<VideoMetadata> videos,
            IMongoCollection<JobMetadata> jobs,
            RabbitMqPublisher publisher,
            CancellationToken cancellationToken) =>
        {
            var video = await videos.Find(item => item.Id == id).FirstOrDefaultAsync(cancellationToken);
            if (video is null)
            {
                return Results.NotFound(new { error = $"Video '{id}' was not found." });
            }

            var request = context.Request.ContentLength is > 0
                ? await context.Request.ReadFromJsonAsync<CreateJobRequest>(cancellationToken)
                : null;
            var requestedType = request?.JobType ?? "Metadata";
            var jobType = JobTypes.FirstOrDefault(type =>
                string.Equals(type, requestedType, StringComparison.OrdinalIgnoreCase));
            if (jobType is null)
            {
                return Results.BadRequest(new { error = "JobType must be Thumbnail, Metadata, or Transcode." });
            }

            var correlationId = context.Response.Headers["X-Correlation-ID"].ToString();
            var now = DateTime.UtcNow;
            var job = new JobMetadata
            {
                Id = Guid.NewGuid().ToString("D"),
                VideoId = video.Id,
                JobType = jobType,
                Status = "Pending",
                Progress = 0,
                CreatedAt = now,
                UpdatedAt = now,
                CorrelationId = correlationId
            };

            await jobs.InsertOneAsync(job, cancellationToken: cancellationToken);
            try
            {
                await publisher.PublishAsync(
                    new VideoJobMessage(job.Id, video.Id, video.FileName, job.JobType, correlationId),
                    cancellationToken);
            }
            catch
            {
                await jobs.UpdateOneAsync(
                    item => item.Id == job.Id,
                    Builders<JobMetadata>.Update
                        .Set(item => item.Status, "Failed")
                        .Set(item => item.ErrorMessage, "The job could not be published to the message broker.")
                        .Set(item => item.UpdatedAt, DateTime.UtcNow),
                    cancellationToken: CancellationToken.None);
                await videos.UpdateOneAsync(
                    item => item.Id == video.Id,
                    Builders<VideoMetadata>.Update.Set(item => item.Status, "Failed"),
                    cancellationToken: CancellationToken.None);
                throw;
            }

            await videos.UpdateOneAsync(
                item => item.Id == video.Id,
                Builders<VideoMetadata>.Update.Set(item => item.Status, "Pending"),
                cancellationToken: cancellationToken);

            return Results.Accepted($"/jobs/{job.Id}", new { jobId = job.Id, job.Status, job.JobType });
        });

        routes.MapGet("/videos", async (
            string? status,
            IMongoCollection<VideoMetadata> videos,
            CancellationToken cancellationToken) =>
        {
            if (status is not null && !VideoStatuses.Any(value =>
                string.Equals(value, status, StringComparison.OrdinalIgnoreCase)))
            {
                return Results.BadRequest(new { error = "Status must be Uploaded, Pending, Processing, Completed, or Failed." });
            }

            var canonicalStatus = status is null
                ? null
                : VideoStatuses.First(value => string.Equals(value, status, StringComparison.OrdinalIgnoreCase));
            var filter = canonicalStatus is null
                ? Builders<VideoMetadata>.Filter.Empty
                : Builders<VideoMetadata>.Filter.Eq(video => video.Status, canonicalStatus);
            var records = await videos.Find(filter)
                .SortByDescending(video => video.UploadedAt)
                .ToListAsync(cancellationToken);
            return Results.Ok(records.Select(ToResponse));
        });

        routes.MapGet("/videos/{id}", async (
            string id,
            IMongoCollection<VideoMetadata> videos,
            IMongoCollection<JobMetadata> jobs,
            CancellationToken cancellationToken) =>
        {
            var video = await videos.Find(item => item.Id == id).FirstOrDefaultAsync(cancellationToken);
            if (video is null)
            {
                return Results.NotFound();
            }

            var relatedJobs = await jobs.Find(item => item.VideoId == id)
                .SortByDescending(item => item.CreatedAt)
                .ToListAsync(cancellationToken);
            return Results.Ok(new VideoDetailResponse(
                video.Id,
                video.FileName,
                video.Status,
                new DateTimeOffset(DateTime.SpecifyKind(video.UploadedAt, DateTimeKind.Utc)),
                relatedJobs));
        });

        routes.MapGet("/jobs/{id}", async (
            string id,
            IMongoCollection<JobMetadata> jobs,
            CancellationToken cancellationToken) =>
        {
            var job = await jobs.Find(item => item.Id == id).FirstOrDefaultAsync(cancellationToken);
            return job is null ? Results.NotFound() : Results.Ok(job);
        });

        routes.MapGet("/jobs/{id}/result", async (
            string id,
            IMongoCollection<JobMetadata> jobs,
            IMinioClient storage,
            CancellationToken cancellationToken) =>
        {
            var job = await jobs.Find(item => item.Id == id).FirstOrDefaultAsync(cancellationToken);
            if (job is null)
            {
                return Results.NotFound();
            }
            if (string.IsNullOrWhiteSpace(job.OutputPath))
            {
                return Results.Conflict(new { error = "This job has no output artifact yet." });
            }

            using var output = new MemoryStream();
            await storage.GetObjectAsync(new GetObjectArgs()
                .WithBucket(bucketName)
                .WithObject(job.OutputPath)
                .WithCallbackStream(stream => stream.CopyTo(output)), cancellationToken);
            var contentType = Path.GetExtension(job.OutputPath).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                _ => "application/json"
            };
            return Results.File(output.ToArray(), contentType, Path.GetFileName(job.OutputPath));
        });
    }

    private static readonly string[] JobTypes = ["Thumbnail", "Metadata", "Transcode"];
    private static readonly string[] VideoStatuses = ["Uploaded", "Pending", "Processing", "Completed", "Failed"];

    private sealed record CreateJobRequest(string JobType);

    private static VideoMetadataResponse ToResponse(VideoMetadata video) => new(
        video.Id,
        video.FileName,
        video.Status,
        new DateTimeOffset(DateTime.SpecifyKind(video.UploadedAt, DateTimeKind.Utc)));
}