using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using WinRT;
using System.Runtime.InteropServices;
using Windows.Storage.Streams;
using Windows.Globalization;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using System.Collections.ObjectModel;
using Microsoft.UI;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace OcrApp
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }
        private async void CaptureOcrButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 捕获屏幕区域
                var picker = new GraphicsCapturePicker();
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                var item = await picker.PickSingleItemAsync();
                if (item == null)
                {
                    ResultTextBox.Text = "未选择捕获源";
                    return;
                }

                // 用 Win2D 获取 Direct3D 设备
                var canvasDevice = CanvasDevice.GetSharedDevice();
                var d3dDevice = canvasDevice as IDirect3DDevice;

                if (d3dDevice == null)
                {
                    ResultTextBox.Text = "无法获取 Direct3D 设备";
                    return;
                }

                var framePool = Direct3D11CaptureFramePool.Create(
                    d3dDevice,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    1,
                    item.Size); var session = framePool.CreateCaptureSession(item);

                // 使用更简单的方法：启动捕获并直接等待帧
                session.StartCapture();

                // 等待一段时间让捕获开始
                await System.Threading.Tasks.Task.Delay(100);

                // 尝试获取帧
                var capturedFrame = framePool.TryGetNextFrame();

                // 如果第一次没获取到，再等待一下重试
                if (capturedFrame == null)
                {
                    await System.Threading.Tasks.Task.Delay(500);
                    capturedFrame = framePool.TryGetNextFrame();
                }

                if (capturedFrame == null)
                {
                    ResultTextBox.Text = "捕获失败：无法获取帧";
                    session.Dispose();
                    framePool.Dispose();
                    return;
                }                // 用 Win2D 转为 SoftwareBitmap
                SoftwareBitmap? bitmap = null;
                try
                {
                    // 使用 Win2D 从 Direct3D 表面创建 CanvasBitmap
                    using var canvasBitmap = CanvasBitmap.CreateFromDirect3D11Surface(canvasDevice, capturedFrame.Surface);
                    // 获取像素数据
                    var pixelBytes = canvasBitmap.GetPixelBytes();

                    // 创建 SoftwareBitmap
                    bitmap = new SoftwareBitmap(
                        BitmapPixelFormat.Bgra8,
                        (int)canvasBitmap.SizeInPixels.Width,
                        (int)canvasBitmap.SizeInPixels.Height,
                        BitmapAlphaMode.Premultiplied);

                    // 将像素数据复制到 SoftwareBitmap
                    bitmap.CopyFromBuffer(pixelBytes.AsBuffer());
                }
                catch (Exception ex)
                {
                    ResultTextBox.Text = $"转换位图失败: {ex.Message}";
                    capturedFrame.Dispose();
                    session.Dispose();
                    framePool.Dispose();
                    return;
                }

                if (bitmap == null)
                {
                    ResultTextBox.Text = "位图转换失败";
                    capturedFrame.Dispose();
                    session.Dispose();
                    framePool.Dispose();
                    return;
                }

                // OCR
                var ocr = OcrEngine.TryCreateFromUserProfileLanguages();
                if (ocr == null)
                {
                    ResultTextBox.Text = "无法创建 OCR 引擎";
                    bitmap.Dispose();
                    capturedFrame.Dispose();
                    session.Dispose();
                    framePool.Dispose();
                    return;
                }

                var result = await ocr.RecognizeAsync(bitmap);
                ResultTextBox.Text = string.IsNullOrEmpty(result.Text) ? "未识别到文本" : result.Text;

                // 清理资源
                bitmap.Dispose();
                capturedFrame.Dispose();
                session.Dispose();
                framePool.Dispose();
            }
            catch (Exception ex)
            {
                ResultTextBox.Text = $"发生错误: {ex.Message}";
            }
        }
    }
}
