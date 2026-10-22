namespace Waikan.Models;

public sealed record RecognitionConfiguration(
    AudioSourceKind Source,
    string? DeviceId,
    int Sensitivity,
    int CooldownMilliseconds = 1000);
