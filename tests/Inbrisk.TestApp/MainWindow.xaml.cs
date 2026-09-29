using System.IO;
using System.Windows;

namespace Inbrisk.TestApp;

public partial class MainWindow : Window
{
    public static readonly string StateDir =
        Path.Combine(Path.GetTempPath(), "inbrisk_testapp");
    public static readonly string StateFile =
        Path.Combine(StateDir, "state.txt");

    public MainWindow()
    {
        InitializeComponent();
        // test harness may place the window on a non-primary monitor so
        // physical-input tests never touch the user's main screen — applied
        // after InitializeComponent or the XAML Left/Top would win
        var pos = Environment.GetEnvironmentVariable("INBRISK_TESTAPP_POS");
        var cmdArgs = Environment.GetCommandLineArgs();
        if (pos == null && cmdArgs.Length > 1) pos = cmdArgs[1];
        if (pos is { })
        {
            var parts = pos.Split(',');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], out var x) &&
                double.TryParse(parts[1], out var y))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = x; Top = y;
            }
        }
        Directory.CreateDirectory(StateDir);
        WriteState("idle");

        for (var i = 1; i <= 40; i++)
        {
            var item = new System.Windows.Controls.ListBoxItem
            {
                Content = $"Song {i:D2}",
            };
            System.Windows.Automation.AutomationProperties.SetName(item, $"Song {i:D2}");
            System.Windows.Automation.AutomationProperties.SetAutomationId(item, $"Song_{i:D2}");
            VirtualizedSongList.Items.Add(item);
        }
    }

    /// <summary>Tests poll this file in tight SpinUntil loops — a read can be
    /// in flight exactly when we write. Retry briefly and never let a sharing
    /// violation escape onto the dispatcher (it would take the app down).</summary>
    internal static void WriteState(string text)
    {
        for (var i = 0; i < 20; i++)
        {
            try
            {
                using var fs = new FileStream(StateFile, FileMode.Create,
                    FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                using var w = new StreamWriter(fs);
                w.Write(text);
                return;
            }
            catch (IOException) { System.Threading.Thread.Sleep(25); }
            catch (UnauthorizedAccessException) { System.Threading.Thread.Sleep(25); }
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        WriteState("saved:" + MainTextBox.Text);
        StatusText.Text = "Saved!";
    }

    private void OnSecondarySave(object sender, RoutedEventArgs e)
    {
        WriteState("saved_secondary:" + MainTextBox.Text);
        StatusText.Text = "Saved in Secondary Panel!";
    }

    private async void OnLoadRemote(object sender, RoutedEventArgs e)
    {
        RemoteItemText.Text = "Loading...";
        System.Windows.Automation.AutomationProperties.SetName(RemoteItemText, "Loading...");
        await System.Threading.Tasks.Task.Delay(300);
        RemoteItemText.Text = "Remote Data Loaded";
        System.Windows.Automation.AutomationProperties.SetName(RemoteItemText, "Remote Data Loaded");
        WriteState("remote:loaded");
    }

    private void OnToggleAnim(object sender, RoutedEventArgs e)
    {
        if (Canvas.Animating) Canvas.StopAnimation(); else Canvas.StartAnimation();
        AnimButton.Content = Canvas.Animating ? "Stop animation" : "Toggle animation";
        WriteState(Canvas.Animating ? "canvas:animating" : "canvas:idle");
    }

    private void OnOpenDialog(object sender, RoutedEventArgs e)
    {
        var dlg = new Window
        {
            Title = "TestDialog",
            Width = 260,
            Height = 160,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Dialog content",
            Margin = new Thickness(0, 0, 0, 12),
        });
        var close = new System.Windows.Controls.Button
        {
            Content = "Close dialog",
            Width = 110,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(close, "CloseDialog");
        System.Windows.Automation.AutomationProperties.SetName(close, "Close dialog");
        close.Click += (_, _) => dlg.Close();
        panel.Children.Add(close);
        dlg.Content = panel;
        dlg.Show(); // non-modal so UIA can still reach the main window
    }
}
