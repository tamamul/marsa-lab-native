using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace MarsaLab.Desktop;

public partial class ViewWindow : Window
{
    readonly Api api;
    readonly string pcName;
    public string ClientId { get; }

    DispatcherTimer? tFrame, tFlush;
    readonly List<InputEv> buf = new();
    bool ctl, got, busy;
    DateTime since = DateTime.Now, lastMove = DateTime.MinValue;

    public ViewWindow(Api api, string clientId, string name)
    {
        InitializeComponent();
        this.api = api;
        ClientId = clientId;
        pcName = name;
        Title = "Layar — " + name;
        TitleText.Text = name;
    }

    void Window_Loaded(object sender, RoutedEventArgs e)
    {
        tFrame = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        tFrame.Tick += async (_, _) => await FrameTick();
        tFrame.Start();
        tFlush = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        tFlush.Tick += async (_, _) => await Flush();
        tFlush.Start();
        _ = FrameTick();
    }

    void Window_Closed(object? sender, EventArgs e)
    {
        tFrame?.Stop();
        tFlush?.Stop();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ===================== Gambar layar =====================
    async Task FrameTick()
    {
        if (busy) return;
        busy = true;
        try
        {
            await api.WatchAsync(new[] { ClientId }, "full", 10);
            var f = await api.ScreenAsync(ClientId);
            var img = Util.Decode(f.Jpg);
            if (img != null)
            {
                Screen.Source = img;
                got = true;
                LoadBox.Visibility = Visibility.Collapsed;
                AgeText.Text = "· diperbarui " + Util.Ago(f.T);
            }
        }
        catch
        {
            if (!got && (DateTime.Now - since).TotalSeconds > 15)
                LoadText.Text = "Belum ada gambar. Pastikan client sudah versi terbaru dan layar PC tidak terkunci.";
        }
        finally { busy = false; }
    }

    // ===================== Kontrol jarak jauh =====================
    void Ctl_Click(object sender, RoutedEventArgs e)
    {
        ctl = !ctl;
        buf.Clear();
        CtlBtn.Content = ctl ? "🖱 Kontrol: AKTIF" : "🖱 Kontrol: Mati";
        CtlBtn.Style = (Style)FindResource(ctl ? "BtnDanger" : "BtnSec");
        CtlBanner.Visibility = ctl ? Visibility.Visible : Visibility.Collapsed;
        Screen.Cursor = ctl ? Cursors.Cross : Cursors.Arrow;
    }

    void Push(InputEv ev)
    {
        buf.Add(ev);
        if (buf.Count >= 60) _ = Flush();
    }

    async Task Flush()
    {
        if (buf.Count == 0) return;
        var batch = buf.Take(60).ToList();
        buf.RemoveRange(0, batch.Count);
        try { await api.ControlAsync(ClientId, batch); }
        catch (Exception ex) { AgeText.Text = "· kontrol gagal: " + ex.Message; }
    }

    (double X, double Y)? Pos(MouseEventArgs e)
    {
        if (Screen.ActualWidth < 1 || Screen.ActualHeight < 1) return null;
        var p = e.GetPosition(Screen);
        return (Math.Clamp(p.X / Screen.ActualWidth, 0, 1), Math.Clamp(p.Y / Screen.ActualHeight, 0, 1));
    }

    static string Btn(MouseButtonEventArgs e) => e.ChangedButton == MouseButton.Right ? "right" : e.ChangedButton == MouseButton.Middle ? "middle" : "left";

    void Screen_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!ctl || Pos(e) is not { } p) return;
        Screen.CaptureMouse();
        Push(new InputEv("down", p.X, p.Y, B: Btn(e)));
        e.Handled = true;
    }

    void Screen_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!ctl || Pos(e) is not { } p) return;
        Screen.ReleaseMouseCapture();
        Push(new InputEv("up", p.X, p.Y, B: Btn(e)));
        e.Handled = true;
    }

    void Screen_MouseMove(object sender, MouseEventArgs e)
    {
        if (!ctl || e.LeftButton != MouseButtonState.Pressed && e.RightButton != MouseButtonState.Pressed) return;
        if ((DateTime.Now - lastMove).TotalMilliseconds < 80 || Pos(e) is not { } p) return;
        lastMove = DateTime.Now;
        Push(new InputEv("move", p.X, p.Y));
    }

    void Screen_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!ctl || Pos(e) is not { } p) return;
        Push(new InputEv("wheel", p.X, p.Y, D: e.Delta > 0 ? 120 : -120));
        e.Handled = true;
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!ctl) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.CapsLock or Key.ImeProcessed or Key.None) return;

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var special = key is Key.Back or Key.Tab or Key.Enter or Key.Escape or Key.Delete or Key.Insert or Key.Home or Key.End
                          or Key.PageUp or Key.PageDown or Key.Left or Key.Right or Key.Up or Key.Down
                      || (key >= Key.F1 && key <= Key.F12);
        var combo = (ctrl || alt) && ((key >= Key.A && key <= Key.Z) || (key >= Key.D0 && key <= Key.D9) || key == Key.Space);
        if (!special && !combo) return; // huruf biasa dikirim lewat PreviewTextInput

        var mods = new List<string>();
        if (ctrl) mods.Add("ctrl");
        if (shift) mods.Add("shift");
        if (alt) mods.Add("alt");
        Push(new InputEv("key", Vk: KeyInterop.VirtualKeyFromKey(key), Mods: mods.ToArray()));
        e.Handled = true;
    }

    void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!ctl || string.IsNullOrEmpty(e.Text)) return;
        Push(new InputEv("text", S: e.Text));
        e.Handled = true;
    }
}
