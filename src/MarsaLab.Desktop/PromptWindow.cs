using System.Windows;
using System.Windows.Controls;

namespace MarsaLab.Desktop;

public sealed class PromptWindow : Window
{
    readonly TextBox box = new();
    string? value;

    PromptWindow(string title, string hint)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Text");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 14;

        var hintText = new TextBlock { Text = hint, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        hintText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        box.Style = (Style)Application.Current.FindResource("Tb");

        var ok = new Button { Content = "Simpan", IsDefault = true, Margin = new Thickness(8, 0, 0, 0), Style = (Style)Application.Current.FindResource("Btn") };
        var cancel = new Button { Content = "Batal", IsCancel = true, Style = (Style)Application.Current.FindResource("BtnSec") };
        ok.Click += (_, _) => { value = box.Text.Trim(); DialogResult = true; };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(hintText);
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => box.Focus();
    }

    public static string? Ask(Window owner, string title, string hint)
    {
        var w = new PromptWindow(title, hint) { Owner = owner };
        return w.ShowDialog() == true && !string.IsNullOrWhiteSpace(w.value) ? w.value : null;
    }
}
