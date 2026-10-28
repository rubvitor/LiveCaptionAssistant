using System.Windows;
using LiveCaptionAssistant.Core.Platform;

namespace LiveCaptionAssistant.App.Views;

/// <summary>Small themed dialog used for notices and confirmations.</summary>
public partial class NoticeWindow : Window
{
    private readonly Uri? _link;
    private readonly IBrowserLauncher _browser;

    public NoticeWindow(string title, string message, string confirmText, string? cancelText, string? linkText, Uri? link, IBrowserLauncher browser)
    {
        _link = link;
        _browser = browser;
        InitializeComponent();

        Title = title;
        HeadingText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;

        if (cancelText is not null)
        {
            CancelButton.Content = cancelText;
            CancelButton.Visibility = Visibility.Visible;
        }

        if (linkText is not null && link is not null)
        {
            LinkText.Text = linkText;
            LinkContainer.Visibility = Visibility.Visible;
        }
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnLinkClick(object sender, RoutedEventArgs e)
    {
        if (_link is not null)
        {
            _browser.Open(_link);
        }
    }
}
