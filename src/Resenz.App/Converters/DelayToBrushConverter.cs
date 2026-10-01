using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Resenz.App.Converters;

/// <summary>
/// 지연 등급(good/medium/bad/none/timeout/testing)을 지연 텍스트 색으로 변환.
/// </summary>
public sealed class DelayToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Good = new(Color.FromArgb(255, 22, 163, 74));
    private static readonly SolidColorBrush Medium = new(Color.FromArgb(255, 217, 119, 6));
    private static readonly SolidColorBrush Bad = new(Color.FromArgb(255, 220, 38, 38));
    private static readonly SolidColorBrush Idle = new(Color.FromArgb(255, 128, 128, 128));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return (value as string) switch
        {
            "good" => Good,
            "medium" => Medium,
            "bad" => Bad,
            // 시간 초과와 연결 실패 모두 이상으로 보고 빨강 강조.
            "timeout" => Bad,
            "none" => Bad,
            _ => Idle,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
