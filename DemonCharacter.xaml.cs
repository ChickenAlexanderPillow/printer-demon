using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace PrinterDemon;

public partial class DemonCharacter : UserControl
{
    private readonly RotateTransform _danceRotation = new();
    private readonly TranslateTransform _danceTranslation = new();

    public DemonCharacter()
    {
        InitializeComponent();
        CharacterImageHost.RenderTransform = new TransformGroup
        {
            Children = { _danceRotation, _danceTranslation }
        };
    }

    public void ShowIdle()
    {
        IdleDemonImage.Visibility = Visibility.Visible;
        DancingDemonImage.Visibility = Visibility.Collapsed;
        CharacterImageHost.Effect = null;
        StopDanceAnimation();
    }

    public void ShowPrinting()
    {
        IdleDemonImage.Visibility = Visibility.Collapsed;
        DancingDemonImage.Visibility = Visibility.Visible;
        CharacterImageHost.Effect = new DropShadowEffect
        {
            Color = Colors.Red,
            BlurRadius = 8,
            ShadowDepth = 0,
            Opacity = 0.55
        };
        _danceRotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
        {
            From = -7,
            To = 7,
            Duration = TimeSpan.FromMilliseconds(520),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        });
        _danceTranslation.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            From = -2,
            To = 2,
            Duration = TimeSpan.FromMilliseconds(520),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        });
    }

    public void ShowDone() => ShowIdle();

    public void ShowError()
    {
        IdleDemonImage.Visibility = Visibility.Visible;
        DancingDemonImage.Visibility = Visibility.Collapsed;
        CharacterImageHost.Effect = null;
        StopDanceAnimation();
        _danceRotation.Angle = -4;
    }

    private void StopDanceAnimation()
    {
        _danceRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        _danceTranslation.BeginAnimation(TranslateTransform.YProperty, null);
        _danceRotation.Angle = 0;
        _danceTranslation.Y = 0;
    }
}
