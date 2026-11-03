using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MediWay.Kiosk.Controls;

// View-only behavior: a new screen begins at its title, while periodic updates preserve scrolling.
public static class ScreenScrollReset
{
    public static readonly DependencyProperty ScreenProperty = DependencyProperty.RegisterAttached(
        "Screen", typeof(object), typeof(ScreenScrollReset), new PropertyMetadata(null, OnScreenChanged));
    public static object? GetScreen(DependencyObject target) => target.GetValue(ScreenProperty);
    public static void SetScreen(DependencyObject target, object? value) => target.SetValue(ScreenProperty,value);
    private static void OnScreenChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (Equals(args.OldValue,args.NewValue) || target is not ScrollViewer viewer) return;
        var screen=args.NewValue;
        viewer.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (Equals(GetScreen(viewer),screen)) viewer.ScrollToTop();
        }));
    }
}