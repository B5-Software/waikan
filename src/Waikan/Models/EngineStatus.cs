namespace Waikan.Models;

public sealed record EngineStatus(
    EngineState State,
    string Detail,
    string? Error = null)
{
    public static EngineStatus Stopped { get; } = new(EngineState.Stopped, "识别已停止");
    public static EngineStatus Paused { get; } = new(EngineState.Paused, "识别已暂停");
}
