using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace OcrApp.Services
{
  public static class ImageProcessingService
  {
    public static SoftwareBitmap? ConvertFrameToBitmap(Direct3D11CaptureFrame capturedFrame)
    {
      try
      {
        using var canvasBitmap = CanvasBitmap.CreateFromDirect3D11Surface(
            CanvasDevice.GetSharedDevice(), capturedFrame.Surface);
        var pixelBytes = canvasBitmap.GetPixelBytes();
        var bitmap = new SoftwareBitmap(
            BitmapPixelFormat.Bgra8,
            (int)canvasBitmap.SizeInPixels.Width,
            (int)canvasBitmap.SizeInPixels.Height,
            BitmapAlphaMode.Premultiplied);
        bitmap.CopyFromBuffer(pixelBytes.AsBuffer());
        return bitmap;
      }
      catch (Exception)
      {
        return null;
      }
    }

    public static async Task<SoftwareBitmap?> CropBitmapAsync(SoftwareBitmap sourceBitmap, RectInt32 region)
    {
      try
      {
        if (region.Width <= 0 || region.Height <= 0 ||
            region.X < 0 || region.Y < 0 ||
            region.X + region.Width > sourceBitmap.PixelWidth ||
            region.Y + region.Height > sourceBitmap.PixelHeight)
        {
          return sourceBitmap;
        }

        using var ms = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ms);
        encoder.SetSoftwareBitmap(sourceBitmap);
        encoder.BitmapTransform.Bounds = new BitmapBounds
        {
          X = (uint)region.X,
          Y = (uint)region.Y,
          Width = (uint)region.Width,
          Height = (uint)region.Height
        };

        await encoder.FlushAsync();
        ms.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(ms);
        var croppedBitmap = await decoder.GetSoftwareBitmapAsync();

        return croppedBitmap;
      }
      catch (Exception)
      {
        return sourceBitmap;
      }
    }

    public static async Task<byte[]?> ConvertBitmapToPngBytesAsync(SoftwareBitmap bitmap)
    {
      try
      {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();

        stream.Seek(0);
        var buffer = new byte[stream.Size];
        var reader = stream.AsStreamForRead();
        await reader.ReadAsync(buffer, 0, buffer.Length);
        return buffer;
      }
      catch (Exception)
      {
        return null;
      }
    }
  }
}