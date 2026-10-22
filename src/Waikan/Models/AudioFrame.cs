namespace Waikan.Models;

public readonly record struct AudioFrame(float[] Samples, DateTimeOffset CapturedAt);
