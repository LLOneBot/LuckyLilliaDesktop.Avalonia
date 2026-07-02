using System;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace LuckyLilliaDesktop.Utils;

public static class PicHelper
{
    public static Bitmap DecodeAssetIconCropped(string assetUri, double displaySize, double renderScaling)
    {
        using var stream = AssetLoader.Open(new Uri(assetUri));
        return DecodeToWidth(stream, displaySize, renderScaling);
    }

    public static Bitmap DecodeToWidth(Stream stream, double displayWidth, double renderScaling)
    {
        var targetSize = Math.Max(1, (int)(displayWidth * renderScaling) * 2);
        return ResizeStreamToBitmap(stream, targetSize, targetSize)
            ?? throw new InvalidOperationException("无法解码图片资源");
    }

    private static Bitmap? ResizeStreamToBitmap(Stream stream, int targetWidth, int targetHeight)
    {
        byte[] originalBytes;
        using (var input = new MemoryStream())
        {
            if (stream.CanSeek)
                stream.Position = 0;
            stream.CopyTo(input);
            originalBytes = input.ToArray();
        }

        using var original = SKBitmap.Decode(originalBytes);
        if (original == null)
            return null;

        var targetRatio = (float)targetWidth / targetHeight;
        var originalRatio = (float)original.Width / original.Height;

        SKRectI cropRect;

        if (originalRatio > targetRatio)
        {
            var cropWidth = (int)(original.Height * targetRatio);
            var cropX = (original.Width - cropWidth) / 2;
            cropRect = new SKRectI(cropX, 0, cropX + cropWidth, original.Height);
        }
        else
        {
            var cropHeight = (int)(original.Width / targetRatio);
            var cropY = (original.Height - cropHeight) / 2;
            cropRect = new SKRectI(0, cropY, original.Width, cropY + cropHeight);
        }

        using var cropped = new SKBitmap(cropRect.Width, cropRect.Height);
        if (!original.ExtractSubset(cropped, cropRect))
            return null;

        var info = new SKImageInfo(targetWidth, targetHeight);
        using var resized = new SKBitmap(info);
        if (!cropped.ScalePixels(resized, SKFilterQuality.High))
            return null;

        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var resizedBytes = data.ToArray();

        using var outputStream = new MemoryStream(resizedBytes);
        return new Bitmap(outputStream);
    }
}
