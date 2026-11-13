using System.Windows;
using System.Windows.Media;

namespace DocumentGenerator;

public enum ConfirmKind { Danger, Primary, Info }

/// <summary>
/// Єдине діалогове вікно підтвердження/повідомлення у стилі Windows 11.
/// Для небезпечних дій (видалення) фокус і Enter за замовчуванням на «Скасувати»; Esc завжди скасовує.
/// </summary>
public partial class ConfirmWindow : Window
{
    private ConfirmWindow(string title, string message, string confirmText, ConfirmKind kind,
        string badgeIconKey, string? confirmIconKey, bool showCancel)
    {
        InitializeComponent();
        Title = title;
        HeadingText.Text = title;
        MessageText.Text = message;
        ConfirmLabel.Text = confirmText;

        var danger = kind == ConfirmKind.Danger;
        BadgeIcon.Data = (Geometry)FindResource(badgeIconKey);
        BadgeBorder.Background = (Brush)FindResource(danger ? "DangerSoft" : "AccentSoft");
        BadgeIcon.Foreground = (Brush)FindResource(danger ? "DangerFg" : "Accent");

        if (confirmIconKey is null) ConfirmIcon.Visibility = Visibility.Collapsed;
        else ConfirmIcon.Data = (Geometry)FindResource(confirmIconKey);
        ConfirmButton.Style = (Style)FindResource(danger ? "DangerSolidButton" : "PrimaryButton");

        CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        ConfirmButton.IsCancel = !showCancel;
        CancelButton.IsDefault = showCancel && danger;
        ConfirmButton.IsDefault = !(showCancel && danger);

        Loaded += (_, _) => (showCancel && danger ? CancelButton : ConfirmButton).Focus();
    }

    /// <summary>Запитує підтвердження. true — користувач підтвердив.</summary>
    public static bool Ask(Window? owner, string title, string message, string confirmText,
        ConfirmKind kind, string badgeIconKey, string? confirmIconKey = null)
    {
        var window = new ConfirmWindow(title, message, confirmText, kind, badgeIconKey, confirmIconKey, true)
        {
            Owner = owner
        };
        return window.ShowDialog() == true;
    }

    /// <summary>Інформаційне повідомлення з однією кнопкою.</summary>
    public static void Notify(Window? owner, string title, string message)
    {
        var window = new ConfirmWindow(title, message, "Гаразд", ConfirmKind.Info, "IconCheck", null, false)
        {
            Owner = owner
        };
        window.ShowDialog();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
