namespace Video.API.Models;

public sealed record VideoDetailResponse(
    string Id,
    string FileName,
    string Status,
    DateTimeOffset UploadedAt,
    IReadOnlyList<JobMetadata> Jobs);