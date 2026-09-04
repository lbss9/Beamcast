using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Beamcast.Controls;

/// <summary>Paints packed BGRA frames into a <see cref="WriteableBitmap"/>. UI thread only.</summary>
public sealed partial class VideoView : UserControl
{
    private WriteableBitmap? _bitmap;

    public VideoView()
    {
        InitializeComponent();
    }

    public bool HasFrame => _bitmap is not null;

    public int FrameWidth => _bitmap?.PixelWidth ?? 0;

    public int FrameHeight => _bitmap?.PixelHeight ?? 0;

    public void Present(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
            return;

        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height);
            Surface.Source = _bitmap;
        }

        using (var stream = _bitmap.PixelBuffer.AsStream())
        {
            stream.Position = 0;
            stream.Write(bgra, 0, width * height * 4);
        }
        _bitmap.Invalidate();
    }

    public void Clear()
    {
        _bitmap = null;
        Surface.Source = null;
    }
}
