using System;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.Foundation; // 添加Point类型的命名空间

namespace OcrApp
{
  public sealed partial class RegionSelector : Window
  {
    private SoftwareBitmap? _capturedBitmap; // 标记为可为空
    private Point _startPoint;
    private bool _isSelecting = false;
    private double _imageScaleFactor = 1.0; // 用于处理图像可能被缩放的情况

    // 用于返回给调用者的选择区域坐标（相对于原始图像）
    public Windows.Graphics.RectInt32? SelectedRegion { get; private set; }

    // 当选择完成时触发的事件
    public event EventHandler<Windows.Graphics.RectInt32?> SelectionConfirmed;

    public RegionSelector()
    {
      InitializeComponent();

      // 添加画布的事件处理
      ImageCanvas.PointerPressed += ImageCanvas_PointerPressed;
      ImageCanvas.PointerMoved += ImageCanvas_PointerMoved;
      ImageCanvas.PointerReleased += ImageCanvas_PointerReleased;

      // 默认没有选择区域
      SelectedRegion = null;
    }

    // 设置要显示的图像
    public async void SetCapturedBitmap(SoftwareBitmap bitmap)
    {
      _capturedBitmap = bitmap;

      // 将SoftwareBitmap转换为可显示的BitmapImage
      var stream = new InMemoryRandomAccessStream();
      var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
      encoder.SetSoftwareBitmap(bitmap);
      await encoder.FlushAsync();

      var bitmapImage = new BitmapImage();
      stream.Seek(0);
      await bitmapImage.SetSourceAsync(stream);

      PreviewImage.Source = bitmapImage;
      PreviewImage.Width = bitmap.PixelWidth;
      PreviewImage.Height = bitmap.PixelHeight;

      // 如果画布尺寸小于图像，可能需要调整图像缩放比例
      CalcImageScaleFactor();
    }

    // 计算图像的缩放因子，用于将选择坐标转换为原始图像坐标
    private void CalcImageScaleFactor()
    {
      if (_capturedBitmap == null || PreviewImage.ActualWidth == 0)
        return;

      _imageScaleFactor = _capturedBitmap.PixelWidth / PreviewImage.ActualWidth;
    }

    private void ImageCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
      _isSelecting = true;
      _startPoint = e.GetCurrentPoint(ImageCanvas).Position;

      // 重置选择框
      SelectionRectangle.Width = 0;
      SelectionRectangle.Height = 0;
      Canvas.SetLeft(SelectionRectangle, _startPoint.X);
      Canvas.SetTop(SelectionRectangle, _startPoint.Y);

      // 捕获指针
      ImageCanvas.CapturePointer(e.Pointer);
    }

    private void ImageCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
      if (!_isSelecting)
        return;

      var currentPoint = e.GetCurrentPoint(ImageCanvas).Position;

      // 计算选择矩形的尺寸和位置
      double left = Math.Min(_startPoint.X, currentPoint.X);
      double top = Math.Min(_startPoint.Y, currentPoint.Y);
      double width = Math.Abs(currentPoint.X - _startPoint.X);
      double height = Math.Abs(currentPoint.Y - _startPoint.Y);

      // 更新选择矩形
      Canvas.SetLeft(SelectionRectangle, left);
      Canvas.SetTop(SelectionRectangle, top);
      SelectionRectangle.Width = width;
      SelectionRectangle.Height = height;
    }

    private void ImageCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
      if (!_isSelecting)
        return;

      _isSelecting = false;

      // 释放指针捕获
      ImageCanvas.ReleasePointerCapture(e.Pointer);

      // 计算选择区域（相对于原始图像的坐标）
      CalculateSelectedRegion();
    }

    private void CalculateSelectedRegion()
    {
      if (SelectionRectangle.Width <= 0 || SelectionRectangle.Height <= 0)
      {
        SelectedRegion = null;
        return;
      }

      // 获取选择框在Canvas中的坐标
      double left = Canvas.GetLeft(SelectionRectangle);
      double top = Canvas.GetTop(SelectionRectangle);

      // 转换为原始图像的坐标（考虑可能的缩放）
      int x = (int)(left * _imageScaleFactor);
      int y = (int)(top * _imageScaleFactor);
      int width = (int)(SelectionRectangle.Width * _imageScaleFactor);
      int height = (int)(SelectionRectangle.Height * _imageScaleFactor);

      // 确保不超出边界
      if (_capturedBitmap != null)
      {
        x = Math.Max(0, Math.Min(x, _capturedBitmap.PixelWidth - 1));
        y = Math.Max(0, Math.Min(y, _capturedBitmap.PixelHeight - 1));
        width = Math.Min(width, _capturedBitmap.PixelWidth - x);
        height = Math.Min(height, _capturedBitmap.PixelHeight - y);
      }

      // 设置选择区域
      SelectedRegion = new Windows.Graphics.RectInt32(x, y, width, height);
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
      // 触发选择确认事件
      SelectionConfirmed?.Invoke(this, SelectedRegion);

      // 关闭窗口
      this.Close();
    }

    private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
    {
      // 清除选择
      SelectionRectangle.Width = 0;
      SelectionRectangle.Height = 0;
      SelectedRegion = null;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
      // 取消选择，返回null
      SelectedRegion = null;
      SelectionConfirmed?.Invoke(this, null);

      // 关闭窗口
      this.Close();
    }
  }
}
