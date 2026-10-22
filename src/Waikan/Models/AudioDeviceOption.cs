using Waikan.Infrastructure;

namespace Waikan.Models;

public sealed class AudioDeviceOption : ObservableObject
{
    private string? _id;
    private string _name = string.Empty;
    private bool _isDefault;
    private bool _isAvailable = true;

    public AudioDeviceOption(string? id, string name, bool isDefault = false)
    {
        _id = id;
        _name = name;
        _isDefault = isDefault;
    }

    public string? Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public bool IsDefault
    {
        get => _isDefault;
        set => SetProperty(ref _isDefault, value);
    }

    public bool IsAvailable
    {
        get => _isAvailable;
        set => SetProperty(ref _isAvailable, value);
    }

    public override string ToString() => Name;
}

public sealed class SelectionOption<T>
{
    public SelectionOption(T value, string label)
    {
        Value = value;
        Label = label;
    }

    public T Value { get; }

    public string Label { get; }

    public override string ToString() => Label;
}
