using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Resenz.App.Converters;

/// <summary>
/// 노드 선택 상태 배경: 선택 시 반투명 강조색, 미선택 시 투명.
/// </summary>
public sealed class SelectedToBrushConverter : IValueConverter
{
    private static SolidColorBrush? _selected;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not bool selected || !selected) return Transparent();

        if (_selected is null)
        {
            _selected = new SolidColorBrush(Color.FromArgb(48, 0, 120, 212));
            try
            {
                if (Application.Current?.Resources is { } res
                    && res.TryGetValue("SystemAccentColor", out var accent)
                    && accent is Color color)
                {
                    _selected = new SolidColorBrush(Color.FromArgb(48, color.R, color.G, color.B));
                }
            }
            catch
            {
            }
        }

        return _selected;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();

    internal static SolidColorBrush Transparent() => new(Color.FromArgb(0, 0, 0, 0));
}

/// <summary>
/// 노드 선택 상태 외곽선: 선택 시 강조색, 미선택 시 투명(기본 외곽선은 바깥 ThemeResource가 제공).
/// </summary>
public sealed class SelectedBorderBrushConverter : IValueConverter
{
    private static SolidColorBrush? _selected;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not bool selected || !selected)
            return SelectedToBrushConverter.Transparent();

        if (_selected is null)
        {
            _selected = new SolidColorBrush(Color.FromArgb(255, 0, 120, 212));
            try
            {
                if (Application.Current?.Resources is { } res
                    && res.TryGetValue("SystemAccentColor", out var accent)
                    && accent is Color color)
                {
                    _selected = new SolidColorBrush(color);
                }
            }
            catch
            {
            }
        }

        return _selected;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
