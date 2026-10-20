using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        var dir = AppContext.BaseDirectory;
        var adminExe = Path.Combine(dir, "MarsaLab.Admin.exe");
        Process? server = null;
        if (File.Exists(adminExe))
            server = Process.Start(new ProcessStartInfo(adminExe, "--no-browser")
                { WorkingDirectory = dir, CreateNoWindow = true, UseShellExecute = false });

        var form = new Form { Text = "MARSA Lab", Width = 1280, Height = 800, StartPosition = FormStartPosition.CenterScreen };
        var web = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(web);
        const string url = "http://localhost:5080";

        form.Load += async (_, _) =>
        {
            try
            {
                var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarsaLab", "WebView");
                await web.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(null, data));
                web.CoreWebView2.Navigate(url);
            }
            catch (Exception ex)
            {
                MessageBox.Show("WebView2 gagal dimuat:\n" + ex.Message, "MARSA Lab");
            }
        };
        // Server belum siap saat pertama dibuka: coba lagi tiap 1 detik
        web.NavigationCompleted += async (_, e) =>
        {
            if (!e.IsSuccess) { await Task.Delay(1000); web.CoreWebView2.Navigate(url); }
        };
        form.FormClosed += (_, _) => { try { server?.Kill(true); } catch { } };
        Application.Run(form);
    }
}