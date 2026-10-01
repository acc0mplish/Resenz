using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Resenz.App.Converters;

/// <summary>
/// ms-appx SVG 리소스 경로를 SvgImageSource로 변환해 Image에서 국기 표시에 사용.
/// Image.Source SVG 직접 바인딩은 BitmapImage 디코딩을 거쳐 크래시가 나므로 반드시 이 컨버터를 거쳐야 함.
/// </summary>
public sealed class SvgImageSourceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string uri || string.IsNullOrWhiteSpace(uri)) return null!;
        try
        {
            if (uri.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                return new SvgImageSource(new Uri(uri));
            return new BitmapImage(new Uri(uri));
        }
        catch
        {
            return null!;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
