using System.Windows;

namespace PrinterDemon;

public partial class QueueWindow : Window
{
    public QueueWindow()
    {
        InitializeComponent();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Keep the queue usable: only the upper header/title area moves the
        // window, while list rows and buttons retain their normal behavior.
        if (e.OriginalSource is System.Windows.Controls.Button || e.GetPosition(this).Y > 72)
            return;

        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            DragMove();
    }

}
