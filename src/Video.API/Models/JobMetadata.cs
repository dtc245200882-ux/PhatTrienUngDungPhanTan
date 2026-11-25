using MongoDB.Bson.Serialization.Attributes;

namespace Video.API.Models;

public sealed class JobMetadata
{
    [BsonId]
    public string Id { get; set; } = string.Empty;
    public string VideoId { get; set; } = string.Empty;
    public string JobType { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public int Progress { get; set; }
    public string? OutputPath { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
}