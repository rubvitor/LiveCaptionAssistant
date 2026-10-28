using System.Windows;
using System.Windows.Controls;

namespace LiveCaptionAssistant.App.Behaviors;

/// <summary>
/// Keeps a <see cref="ScrollViewer"/> pinned to the bottom while new content arrives,
/// unless the user has scrolled up to read older content.
/// </summary>
public static class AutoScroll
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(AutoScroll), new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty IsPinnedToEndProperty = DependencyProperty.RegisterAttached(
        "IsPinnedToEnd", typeof(bool), typeof(AutoScroll), new PropertyMetadata(true));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not ScrollViewer scrollViewer)
        {
            return;
        }

        if ((bool)args.NewValue)
        {
            scrollViewer.ScrollChanged += OnScrollChanged;
        }
        else
        {
            scrollViewer.ScrollChanged -= OnScrollChanged;
        }
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs args)
    {
        var scrollViewer = (ScrollViewer)sender;
        if (args.ExtentHeightChange == 0)
        {
            scrollViewer.SetValue(IsPinnedToEndProperty, scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 2);
        }
        else if ((bool)scrollViewer.GetValue(IsPinnedToEndProperty))
        {
            scrollViewer.ScrollToEnd();
        }
    }
}
