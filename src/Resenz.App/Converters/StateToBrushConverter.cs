using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Resenz.App.Converters;

/// <summary>
/// 코어 상태 식별자(running/starting/stopping/error/stopped)를 상태 표시 색으로 변환.
/// </summary>
public sealed class StateToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Running = new(Color.FromArgb(255, 34, 197, 94));
    private static readonly SolidColorBrush Warning = new(Color.FromArgb(255, 245, 158, 11));
    private static readonly SolidColorBrush Danger = new(Color.FromArgb(255, 239, 68, 68));
    private static readonly SolidColorBrush Idle = new(Color.FromArgb(255, 128, 128, 128));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return (value as string) switch
        {
            "running" => Running,
            "starting" or "stopping" => Warning,
            "error" => Danger,
            _ => Idle,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
