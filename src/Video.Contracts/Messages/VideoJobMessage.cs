namespace Video.Contracts.Messages;

public sealed record VideoJobMessage(
    string JobId,
    string VideoId,
    string FileName,
    string JobType,
    string CorrelationId);