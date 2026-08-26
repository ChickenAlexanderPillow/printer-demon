using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace PrinterDemon;

public partial class DemonCharacter : UserControl
{
    private readonly DispatcherTimer _danceTimer;
    private bool _dancePose;

    public DemonCharacter()
    {
        InitializeComponent();
        _danceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _danceTimer.Tick += (_, _) =>
        {
            _dancePose = !_dancePose;
            var transform = new RotateTransform(_dancePose ? -9 : 9);
            CharacterImageHost.RenderTransform = transform;
            CharacterImageHost.Margin = new Thickness(0, _dancePose ? -3 : 3, 0, 0);
        };
    }

    public void ShowIdle()
    {
        _danceTimer.Stop();
        IdleDemonImage.Visibility = Visibility.Visible;
        DancingDemonImage.Visibility = Visibility.Collapsed;
        CharacterImageHost.Effect = null;
        CharacterImageHost.RenderTransform = new RotateTransform(0);
        CharacterImageHost.Margin = new Thickness(0);
    }

    public void ShowPrinting()
    {
        IdleDemonImage.Visibility = Visibility.Collapsed;
        DancingDemonImage.Visibility = Visibility.Visible;
        CharacterImageHost.Effect = new DropShadowEffect
        {
            Color = Colors.Red,
            BlurRadius = 18,
            ShadowDepth = 0,
            Opacity = 0.9
        };
        _danceTimer.Start();
    }

    public void ShowDone() => ShowIdle();

    public void ShowError()
    {
        _danceTimer.Stop();
        IdleDemonImage.Visibility = Visibility.Visible;
        DancingDemonImage.Visibility = Visibility.Collapsed;
        CharacterImageHost.Effect = null;
        CharacterImageHost.RenderTransform = new RotateTransform(-4);
    }
}
