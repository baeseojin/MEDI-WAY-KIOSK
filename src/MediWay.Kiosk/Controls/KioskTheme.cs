using System.Windows;
using System.Windows.Media;

namespace MediWay.Kiosk.Controls;

// Window-scoped resources: no process-wide events or patient state.
public static class KioskTheme
{
    public static void Apply(ResourceDictionary resources, bool accessible)
    {
        foreach (var size in new[] { 14, 15, 16, 17, 19, 20, 21, 22, 24, 26, 27, 28, 30, 32, 100 })
            resources[$"Font{size}"] = size == 100 ? 100d : size * (accessible ? 1.25 : 1d);
        resources["TouchHeight"] = accessible ? 72d : 58d;
        resources["InputHeight"] = accessible ? 82d : 66d;
        Set("PageBrush", accessible ? "#000000" : "#F3F7FA");
        Set("SurfaceBrush", accessible ? "#000000" : "#FFFFFF");
        Set("TextBrush", accessible ? "#FFFFFF" : "#163947");
        Set("MutedBrush", accessible ? "#FFFFFF" : "#4D626D");
        Set("AccentBrush", accessible ? "#FFFF00" : "#087F8C");
        Set("OnAccentBrush", accessible ? "#000000" : "#FFFFFF");
        Set("BorderBrush", accessible ? "#FFFFFF" : "#C7D8DF");
        Set("ErrorBrush", accessible ? "#FFFF00" : "#A32222");
        void Set(string key, string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze(); resources[key] = brush;
        }
    }
}
