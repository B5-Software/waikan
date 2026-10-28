using System.Text.Json.Serialization;
using Waikan.Infrastructure;

namespace Waikan.Models;

public sealed class AppSettings : ObservableObject
{
    public const string DefaultOverlayColor = "#FFF6F2EE";
    public const int CurrentSchemaVersion = 2;

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
    private bool _cloudSyncEnabled;
    private string _cloudSyncEndpoint = string.Empty;
    private string _cloudSyncKey = string.Empty;

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

    /// <summary>Pushes new occurrences to the paired Cloudflare Pages viewer.</summary>
    public bool CloudSyncEnabled
    {
        get => _cloudSyncEnabled;
        set => SetProperty(ref _cloudSyncEnabled, value);
    }

    /// <summary>Base URL of the Cloudflare Pages viewer, for example https://waikan-view-01.pages.dev.</summary>
    public string CloudSyncEndpoint
    {
        get => _cloudSyncEndpoint;
        set => SetProperty(ref _cloudSyncEndpoint, (value ?? string.Empty).Trim());
    }

    /// <summary>Per-pair client key. Only the hash is stored on the server.</summary>
    public string CloudSyncKey
    {
        get => _cloudSyncKey;
        set => SetProperty(ref _cloudSyncKey, (value ?? string.Empty).Trim());
    }

    [JsonIgnore]
    public bool IsDefaultAudioDevice => string.IsNullOrWhiteSpace(AudioDeviceId);

    [JsonIgnore]
    public bool HasCloudSyncConfiguration =>
        !string.IsNullOrWhiteSpace(CloudSyncEndpoint) && !string.IsNullOrWhiteSpace(CloudSyncKey);

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

        CloudSyncEndpoint = (CloudSyncEndpoint ?? string.Empty).Trim();
        CloudSyncKey = (CloudSyncKey ?? string.Empty).Trim();
    }
}
