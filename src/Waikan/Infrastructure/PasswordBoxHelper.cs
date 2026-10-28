using System.Windows;
using System.Windows.Controls;

namespace Waikan.Infrastructure;

/// <summary>
/// Allows a PasswordBox to participate in a two-way MVVM binding without
/// exposing the password in a plain TextBox.
/// </summary>
public static class PasswordBoxHelper
{
    public static readonly DependencyProperty BoundPasswordProperty = DependencyProperty.RegisterAttached(
        "BoundPassword",
        typeof(string),
        typeof(PasswordBoxHelper),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnBoundPasswordChanged));

    private static readonly DependencyProperty IsUpdatingProperty = DependencyProperty.RegisterAttached(
        "IsUpdating",
        typeof(bool),
        typeof(PasswordBoxHelper));

    public static string GetBoundPassword(DependencyObject element)
    {
        return (string)element.GetValue(BoundPasswordProperty);
    }

    public static void SetBoundPassword(DependencyObject element, string value)
    {
        element.SetValue(BoundPasswordProperty, value);
    }

    private static void OnBoundPasswordChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not PasswordBox passwordBox)
        {
            return;
        }

        passwordBox.PasswordChanged -= OnPasswordChanged;
        if (!(bool)passwordBox.GetValue(IsUpdatingProperty))
        {
            passwordBox.Password = args.NewValue as string ?? string.Empty;
        }

        passwordBox.PasswordChanged += OnPasswordChanged;
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs args)
    {
        var passwordBox = (PasswordBox)sender;
        passwordBox.SetValue(IsUpdatingProperty, true);
        try
        {
            SetBoundPassword(passwordBox, passwordBox.Password);
        }
        finally
        {
            passwordBox.SetValue(IsUpdatingProperty, false);
        }
    }
}
