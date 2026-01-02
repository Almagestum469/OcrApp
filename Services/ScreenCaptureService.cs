using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CoenM.ImageHash;
using CoenM.ImageHash.HashAlgorithms;
using Microsoft.Graphics.Canvas;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace OcrApp.Services
{
  public interface IScreenCaptureService : IAsyncDisposable, IDisposable
  {
    Task<bool> InitializeAsync(GraphicsCaptureItem item);
    Task<SoftwareBitmap?> CaptureBitmapAsync(RectInt32? region, bool useRegion, CancellationToken cancellationToken = default);
    Task<bool> HasImageChangedAsync(byte[]? lastImageData, RectInt32? region, bool useRegion, CancellationToken cancellationToken = default);
    event EventHandler? CaptureFailed;
  }

  public sealed class ScreenCaptureService : IScreenCaptureService
  {
    private GraphicsCaptureItem? _captureItem;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _captureSession;
    private IDirect3DDevice? _d3dDevice;
    private TaskCompletionSource<Direct3D11CaptureFrame?>? _frameCompletionSource;
    private readonly IImageHash _hashAlgorithm = new PerceptualHash();
    private bool _initialized;

    public event EventHandler? CaptureFailed;

    public async Task<bool> InitializeAsync(GraphicsCaptureItem item)
    {
      _captureItem = item ?? throw new ArgumentNullException(nameof(item));

      try
      {
        _captureSession?.Dispose();
        _framePool?.Dispose();

        var canvasDevice = CanvasDevice.GetSharedDevice();
        if (canvasDevice is not IDirect3DDevice d3dDevice)
        {
          OnCaptureFailed();
          return false;
        }

        _d3dDevice = d3dDevice;
        _framePool = Direct3D11CaptureFramePool.Create(
            d3dDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            1,
            _captureItem.Size);

        _framePool.FrameArrived += OnFrameArrived;

        _captureSession = _framePool.CreateCaptureSession(_captureItem);

        try
        {
#pragma warning disable CA1416 // 验证平台兼容性
          _captureSession.IsCursorCaptureEnabled = false;
#pragma warning restore CA1416 // 验证平台兼容性
        }
        catch
        {
        }

        _captureSession.StartCapture();
        _initialized = true;
        return true;
      }
      catch
      {
        _captureSession?.Dispose();
        _framePool?.Dispose();
        _captureSession = null;
        _framePool = null;
        OnCaptureFailed();
        return false;
      }
    }

    public async Task<SoftwareBitmap?> CaptureBitmapAsync(RectInt32? region, bool useRegion, CancellationToken cancellationToken = default)
    {
      if (!_initialized || _captureItem == null)
      {
        OnCaptureFailed();
        return null;
      }

      var capturedFrame = await GetLatestFrameAsync(cancellationToken).ConfigureAwait(false);
      if (capturedFrame == null) return null;

      try
      {
        var bitmap = ImageProcessingService.ConvertFrameToBitmap(capturedFrame);
        if (bitmap == null) return null;

        if (useRegion && region.HasValue)
        {
          var r = region.Value;
          if (r.Width > 0 && r.Height > 0 &&
              r.X >= 0 && r.Y >= 0 &&
              r.X + r.Width <= bitmap.PixelWidth &&
              r.Y + r.Height <= bitmap.PixelHeight)
          {
            bitmap = await ImageProcessingService.CropBitmapAsync(bitmap, r).ConfigureAwait(false);
          }
        }

        return bitmap;
      }
      finally
      {
        capturedFrame.Dispose();
      }
    }

    public async Task<bool> HasImageChangedAsync(byte[]? lastImageData, RectInt32? region, bool useRegion, CancellationToken cancellationToken = default)
    {
      if (lastImageData == null) return true;

      var bitmap = await CaptureBitmapAsync(region, useRegion, cancellationToken).ConfigureAwait(false);
      if (bitmap == null) return false;

      try
      {
        var currentImageData = await ImageProcessingService.ConvertBitmapToPngBytesAsync(bitmap).ConfigureAwait(false);
        if (currentImageData == null) return false;

        using var lastStream = new MemoryStream(lastImageData);
        using var currentStream = new MemoryStream(currentImageData);

        var hash1 = _hashAlgorithm.Hash(lastStream);
        var hash2 = _hashAlgorithm.Hash(currentStream);
        var similarity = CompareHash.Similarity(hash1, hash2);

        return similarity < 99.0;
      }
      catch
      {
        return true;
      }
      finally
      {
        bitmap.Dispose();
      }
    }

    private async Task<Direct3D11CaptureFrame?> GetLatestFrameAsync(CancellationToken cancellationToken)
    {
      if (_framePool == null)
        return null;

      Direct3D11CaptureFrame? latestFrame = null;
      Direct3D11CaptureFrame? currentFrame;

      while ((currentFrame = _framePool.TryGetNextFrame()) != null)
      {
        latestFrame?.Dispose();
        latestFrame = currentFrame;
      }

      if (latestFrame != null)
        return latestFrame;

      _frameCompletionSource = new TaskCompletionSource<Direct3D11CaptureFrame?>(TaskCreationOptions.RunContinuationsAsynchronously);

      using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      using var timeoutCts = new CancellationTokenSource(1000);
      using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeoutCts.Token);
      using var reg = combinedCts.Token.Register(() => _frameCompletionSource?.TrySetResult(null));

      try
      {
        return await _frameCompletionSource.Task.ConfigureAwait(false);
      }
      finally
      {
        _frameCompletionSource = null;
      }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
      if (_frameCompletionSource != null)
      {
        var frame = sender.TryGetNextFrame();
        _frameCompletionSource.SetResult(frame);
        _frameCompletionSource = null;
      }
      else
      {
        Direct3D11CaptureFrame? frame;
        while ((frame = sender.TryGetNextFrame()) != null)
        {
          frame.Dispose();
        }
      }
    }

    private void OnCaptureFailed()
    {
      CaptureFailed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
      _captureSession?.Dispose();
      _framePool?.Dispose();
      _frameCompletionSource = null;
      _d3dDevice = null;
      _initialized = false;
    }

    public ValueTask DisposeAsync()
    {
      Dispose();
      return ValueTask.CompletedTask;
    }
  }
}
