using System.Runtime.InteropServices;
using Waikan.Services;

namespace Waikan.Tests;

public sealed class AudioCaptureAccessTests
{
    [Fact]
    public void Detects_Access_Denied_In_Exception_Chain()
    {
        Assert.True(AudioCaptureService.IsAccessDenied(new UnauthorizedAccessException()));
        Assert.True(AudioCaptureService.IsAccessDenied(
            new InvalidOperationException("outer", new UnauthorizedAccessException("inner"))));
        Assert.True(AudioCaptureService.IsAccessDenied(
            new COMException("denied", unchecked((int)0x80070005))));
        Assert.False(AudioCaptureService.IsAccessDenied(new InvalidOperationException("other")));
    }

    [Fact]
    public void Access_Denied_Exception_Carries_Audio_Source()
    {
        var exception = new AudioCaptureAccessDeniedException(
            Waikan.Models.AudioSourceKind.Microphone,
            new UnauthorizedAccessException());

        Assert.Equal(Waikan.Models.AudioSourceKind.Microphone, exception.AudioSource);
        Assert.Contains("麦克风", exception.Message);
        Assert.IsType<UnauthorizedAccessException>(exception.InnerException);
    }
}
