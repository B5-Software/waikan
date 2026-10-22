namespace Waikan.Models;

public enum AudioSourceKind
{
    Microphone,
    SystemLoopback,
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public enum MainPage
{
    Today,
    Records,
    Settings,
    About,
}

public enum EngineState
{
    Stopped,
    Loading,
    Listening,
    Paused,
    Error,
}
