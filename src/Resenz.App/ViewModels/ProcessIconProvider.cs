using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Resenz.App.ViewModels;

/// <summary>
/// 프로세스 실행 파일의 큰 아이콘을 추출해 연결 목록 표시에 사용. 경로별 캐시,
/// Win32 SHGetFileInfo로 HICON을 얻은 뒤 System.Drawing으로 PNG로 변환해 WinUI에 전달.
/// </summary>
public static class ProcessIconProvider
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> Order = new();
    private static readonly object Gate = new();
    private const int MaxEntries = 256;

    public static ImageSource? Get(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath)) return null;

        lock (Gate)
        {
            if (Cache.TryGetValue(processPath, out var cached)) return cached;
        }

        ImageSource? source = null;
        try
        {
            source = ExtractIcon(processPath);
        }
        catch
        {
            source = null;
        }

        lock (Gate)
        {
            if (!Cache.ContainsKey(processPath))
            {
                // 상한이 없으면 연결 기록의 프로세스 경로가 계속 늘어남. FIFO로 가장 오래된 항목 퇴출.
                if (Order.Count >= MaxEntries)
                {
                    var oldest = Order.Dequeue();
                    Cache.Remove(oldest);
                }
                Order.Enqueue(processPath);
            }
            Cache[processPath] = source;
        }
        return source;
    }

    private static ImageSource? ExtractIcon(string path)
    {
        // 주의: File.Exists 검사 안 함 —— 연결 기록의 프로세스 경로는 종료된 프로세스/가상 경로를 가리키는 경우가 많아
        // SHGFI_USEFILEATTRIBUTES 이런 경우에도 확장자 기준 연결 아이콘 반환 허용.
        var info = new SHFILEINFO();
        var flags = SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES;
        var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

        try
        {
            using var icon = System.Drawing.Icon.FromHandle(info.hIcon);
            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            stream.Position = 0;

            var image = new BitmapImage();
            var ras = stream.AsRandomAccessStream();
            _ = image.SetSourceAsync(ras);
            return image;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
