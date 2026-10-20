using System.Windows;
using System.Windows.Media;

namespace MarsaLab.Desktop;

public partial class App : Application
{
    public static void ApplyTheme(bool dark)
    {
        void Set(string key, string hex) => Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        if (dark)
        {
            Set("Bg", "#0B141A"); Set("Panel", "#13232B"); Set("Panel2", "#1A2F39"); Set("Line", "#29424C");
            Set("Text", "#EEF8FA"); Set("Muted", "#9BB1BA"); Set("Accent", "#00C2D1"); Set("OnAccent", "#00272B");
        }
        else
        {
            Set("Bg", "#EEF3F5"); Set("Panel", "#FFFFFF"); Set("Panel2", "#F3F7F8"); Set("Line", "#D5E0E4");
            Set("Text", "#10242B"); Set("Muted", "#5D747D"); Set("Accent", "#008A96"); Set("OnAccent", "#FFFFFF");
        }
    }
}
