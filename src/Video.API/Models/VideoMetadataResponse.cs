namespace Video.API.Models;

/// <summary>Video metadata returned by the API.</summary>
public sealed record VideoMetadataResponse(
    string Id,
    string FileName,
    string Status,
    DateTimeOffset UploadedAt);