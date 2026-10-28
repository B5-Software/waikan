namespace Waikan.Models;

public sealed record OccurrenceRecord(
    long Id,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset LocalTime,
    string LocalDay,
    string Source,
    string Keyword,
    string SyncId = "",
    bool Synced = false)
{
    public string SourceLabel => Source == AudioCaptureSourceSystem
        ? "系统声音"
        : "麦克风";

    public const string AudioCaptureSourceSystem = "system";
    public const string AudioCaptureSourceMicrophone = "microphone";
}
