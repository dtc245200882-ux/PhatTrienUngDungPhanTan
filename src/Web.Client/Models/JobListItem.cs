namespace Web.Client.Models;

public sealed record JobListItem(
    string Id,
    string VideoId,
    string JobType,
    string Status,
    int Progress,
    string? OutputPath,
    string? Result,
    string? Error,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string CorrelationId);
