using System.Windows;

namespace PrinterDemon;

public partial class CloseWarningWindow : Window
{
    public CloseWarningWindow(int remainingJobs)
    {
        InitializeComponent();
        var noun = remainingJobs == 1 ? "job is" : "jobs are";
        WarningText.Text = $"There {noun} still queued or in progress. Close Printer Demon anyway?";
    }

    private void KeepOpen_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void CloseAnyway_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
