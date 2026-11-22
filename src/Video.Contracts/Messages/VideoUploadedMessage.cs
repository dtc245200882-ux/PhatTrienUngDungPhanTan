namespace Video.Contracts.Messages;

public sealed record VideoUploadedMessage(string VideoId, string FileName);