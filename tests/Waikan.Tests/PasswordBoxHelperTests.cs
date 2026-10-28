using System.Windows.Controls;
using Waikan.Infrastructure;

namespace Waikan.Tests;

public sealed class PasswordBoxHelperTests
{
    [Fact]
    public void Attach_Tracks_Typed_Password_When_Initial_Value_Is_Empty()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var box = new PasswordBox();
                PasswordBoxHelper.SetAttach(box, true);

                box.Password = "wk_typed_key";
                Assert.Equal("wk_typed_key", PasswordBoxHelper.GetBoundPassword(box));

                box.Password = string.Empty;
                Assert.Equal(string.Empty, PasswordBoxHelper.GetBoundPassword(box));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "PasswordBox STA thread timed out.");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }
}
