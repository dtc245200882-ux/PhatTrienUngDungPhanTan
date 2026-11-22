namespace Web.Client.Models;

public sealed record VideoListItem(
    string Id,
    string FileName,
    string Status,
    DateTimeOffset UploadedAt);