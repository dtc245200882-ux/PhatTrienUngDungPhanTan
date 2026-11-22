using MongoDB.Bson.Serialization.Attributes;

namespace Video.Worker.Models;

public sealed class VideoMetadata
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public DateTime UploadedAt { get; set; }
}