using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System;
using System.IO;

namespace LuckyLilliaDesktop.Utils;

public static class BitmapLoader
{
    public static Bitmap DecodeAssetToWidth(string assetUri, double displayWidth, double renderScaling)
    {
        using var stream = AssetLoader.Open(new Uri(assetUri));
        return DecodeToWidth(stream, displayWidth, renderScaling);
    }

    public static Bitmap DecodeToWidth(Stream stream, double displayWidth, double renderScaling)
    {
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(displayWidth * renderScaling));
        return Bitmap.DecodeToWidth(stream, pixelWidth, BitmapInterpolationMode.HighQuality);
    }
}
