using System.Text.Json.Serialization;
using Waikan.Infrastructure;

namespace Waikan.Models;

public sealed class AppSettings : ObservableObject
{
    public const string DefaultOverlayColor = "#FFF6F2EE";
    public const int CurrentSchemaVersion = 1;

    private int _schemaVersion = CurrentSchemaVersion;
    private bool _startWithWindows = true;
    private bool _minimizeToTrayOnClose = true;
    private bool _recognitionPaused;
    private AudioSourceKind _audioSource = AudioSourceKind.Microphone;
    private string? _audioDeviceId;
    private int _detectionSensitivity = 50;
    private bool _overlayEnabled = true;
    private string _overlayTextColor = DefaultOverlayColor;
    private double _overlayFontSize = 40;
    private AppTheme _theme = AppTheme.System;
    private bool _hasShownTrayHint;

    public int SchemaVersion
    {
        get => _schemaVersion;
        set => SetProperty(ref _schemaVersion, value);
    }

    /// <summary>Registers a per-user Run entry at sign-in.</summary>
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetProperty(ref _startWithWindows, value);
    }

    /// <summary>When the app is started by Windows, keep the main window hidden.</summary>
    public bool MinimizeToTrayOnClose
    {
        get => _minimizeToTrayOnClose;
        set => SetProperty(ref _minimizeToTrayOnClose, value);
    }

    public bool RecognitionPaused
    {
        get => _recognitionPaused;
        set => SetProperty(ref _recognitionPaused, value);
    }

    public AudioSourceKind AudioSource
    {
        get => _audioSource;
        set => SetProperty(ref _audioSource, value);
    }

    /// <summary>Null means the Windows default device for the selected source.</summary>
    public string? AudioDeviceId
    {
        get => _audioDeviceId;
        set => SetProperty(ref _audioDeviceId, value);
    }

    /// <summary>0 = strict, 100 = very sensitive. 50 is the calibrated default.</summary>
    public int DetectionSensitivity
    {
        get => _detectionSensitivity;
        set => SetProperty(ref _detectionSensitivity, Math.Clamp(value, 0, 100));
    }

    public bool OverlayEnabled
    {
        get => _overlayEnabled;
        set => SetProperty(ref _overlayEnabled, value);
    }

    public string OverlayTextColor
    {
        get => _overlayTextColor;
        set => SetProperty(ref _overlayTextColor, value);
    }

    public double OverlayFontSize
    {
        get => _overlayFontSize;
        set => SetProperty(ref _overlayFontSize, Math.Clamp(value, 12, 240));
    }

    public AppTheme Theme
    {
        get => _theme;
        set => SetProperty(ref _theme, value);
    }

    public bool HasShownTrayHint
    {
        get => _hasShownTrayHint;
        set => SetProperty(ref _hasShownTrayHint, value);
    }

    [JsonIgnore]
    public bool IsDefaultAudioDevice => string.IsNullOrWhiteSpace(AudioDeviceId);

    public AppSettings Clone()
    {
        return (AppSettings)MemberwiseClone();
    }

    public void Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;
        DetectionSensitivity = Math.Clamp(DetectionSensitivity, 0, 100);
        OverlayFontSize = Math.Clamp(OverlayFontSize, 12, 240);
        if (!ColorUtilities.IsValidHexColor(OverlayTextColor))
        {
            OverlayTextColor = DefaultOverlayColor;
        }

        if (!Enum.IsDefined(AudioSource))
        {
            AudioSource = AudioSourceKind.Microphone;
        }

        if (!Enum.IsDefined(Theme))
        {
            Theme = AppTheme.System;
        }
    }
}
