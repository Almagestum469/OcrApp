using System;
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

    // 用于返回给调用者的选择区域坐标（相对于原始图像）
    public Windows.Graphics.RectInt32? SelectedRegion { get; private set; }    // 当选择完成时触发的事件
    public event EventHandler<Windows.Graphics.RectInt32?>? SelectionConfirmed;

    public RegionSelector()
    {
      InitializeComponent();

      // 添加画布的事件处理
      ImageCanvas.PointerPressed += ImageCanvas_PointerPressed;
      ImageCanvas.PointerMoved += ImageCanvas_PointerMoved;
      ImageCanvas.PointerReleased += ImageCanvas_PointerReleased;

      // 默认没有选择区域
      SelectedRegion = null;
    }    // 设置要显示的图像
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
      // 移除固定宽高设置，让图片自适应
      // PreviewImage.Width = bitmap.PixelWidth;
      // PreviewImage.Height = bitmap.PixelHeight;
    }    // 获取图片在Canvas中的实际显示区域（考虑Uniform缩放和居中）
    private Rect GetImageDisplayRect()
    {
      if (_capturedBitmap == null || PreviewImage.ActualWidth == 0 || PreviewImage.ActualHeight == 0)
        return new Rect(0, 0, 0, 0);

      // 由于Image使用Stretch="Uniform"，我们可以直接使用Image的ActualWidth和ActualHeight
      // 以及它在父容器中的位置来计算显示区域
      double imageWidth = PreviewImage.ActualWidth;
      double imageHeight = PreviewImage.ActualHeight;

      // 获取Image在其父容器中的位置（应该是居中的）
      var transform = PreviewImage.TransformToVisual(ImageCanvas);
      var imagePosition = transform.TransformPoint(new Point(0, 0));

      return new Rect(imagePosition.X, imagePosition.Y, imageWidth, imageHeight);
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

      var displayRect = GetImageDisplayRect();
      if (displayRect.Width <= 0 || displayRect.Height <= 0 || _capturedBitmap == null)
      {
        SelectedRegion = null;
        return;
      }

      // 获取选择框在Canvas中的坐标
      double selectionLeft = Canvas.GetLeft(SelectionRectangle);
      double selectionTop = Canvas.GetTop(SelectionRectangle);

      // 转换为相对于图片显示区域的坐标
      double relativeLeft = selectionLeft - displayRect.X;
      double relativeTop = selectionTop - displayRect.Y;

      // 计算缩放比例
      double scale = displayRect.Width / _capturedBitmap.PixelWidth;

      // 转换为原始图像坐标
      int x = (int)(relativeLeft / scale);
      int y = (int)(relativeTop / scale);
      int width = (int)(SelectionRectangle.Width / scale);
      int height = (int)(SelectionRectangle.Height / scale);

      // 确保不超出边界
      x = Math.Max(0, Math.Min(x, _capturedBitmap.PixelWidth - 1));
      y = Math.Max(0, Math.Min(y, _capturedBitmap.PixelHeight - 1));
      width = Math.Min(width, _capturedBitmap.PixelWidth - x);
      height = Math.Min(height, _capturedBitmap.PixelHeight - y);

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
