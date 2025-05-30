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
using OcrApp.Utils; // 添加对新命名空间的引用

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace OcrApp
{    /// <summary>
     /// An empty window that can be used on its own or navigated to within a Frame.
     /// </summary>
    public sealed partial class MainWindow : Window
    {
        private GraphicsCaptureItem? _captureItem;
        private OcrEngine? _ocrEngine;
        private SoftwareBitmap? _lastCapturedBitmap;
        private bool _isPaddleOcrInitialized = false; public MainWindow()
        {
            InitializeComponent();
            OcrEngineComboBox.SelectionChanged += OcrEngineComboBox_SelectionChanged;
        }

        private async void OcrEngineComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selectedItem = OcrEngineComboBox.SelectedItem as ComboBoxItem;
            var engineType = selectedItem?.Tag?.ToString();

            if (engineType == "Paddle")
            {
                EngineStatusText.Text = "正在初始化PaddleOCR...";
                EngineStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange);

                var success = await PaddleOcrHelper.InitializeAsync();
                if (success)
                {
                    _isPaddleOcrInitialized = true;
                    EngineStatusText.Text = "PaddleOCR就绪";
                    EngineStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green);
                }
                else
                {
                    _isPaddleOcrInitialized = false;
                    EngineStatusText.Text = "PaddleOCR初始化失败";
                    EngineStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
                }
            }
            else
            {
                EngineStatusText.Text = "Windows OCR就绪";
                EngineStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green);
            }
        }
        private async void SelectCaptureItemButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new GraphicsCapturePicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            _captureItem = await picker.PickSingleItemAsync();
            if (_captureItem != null)
            {
                ResultListView.ItemsSource = new List<string> { $"已选择: {_captureItem.DisplayName}" };

                // 检查当前选择的OCR引擎
                var selectedItem = OcrEngineComboBox.SelectedItem as ComboBoxItem;
                var engineType = selectedItem?.Tag?.ToString();

                if (engineType == "Windows")
                {
                    // 初始化 Windows OCR 引擎
                    var desiredLanguage = new Windows.Globalization.Language("en-US");
                    _ocrEngine = OcrEngine.TryCreateFromLanguage(desiredLanguage);
                    if (_ocrEngine == null)
                    {
                        ResultListView.ItemsSource = new List<string> { $"无法使用指定语言 ({desiredLanguage.DisplayName}) 创建 OCR 引擎，尝试使用用户默认语言。" };
                        _ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
                    }
                    if (_ocrEngine == null)
                    {
                        ResultListView.ItemsSource = new List<string> { "无法创建 Windows OCR 引擎" };
                        RecognizeButton.IsEnabled = false;
                        return;
                    }
                }

                RecognizeButton.IsEnabled = true;
            }
            else
            {
                ResultListView.ItemsSource = new List<string> { "未选择捕获源" };
                RecognizeButton.IsEnabled = false;
            }
        }
        private async void RecognizeButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedItem = OcrEngineComboBox.SelectedItem as ComboBoxItem;
            var engineType = selectedItem?.Tag?.ToString();

            if (_captureItem == null)
            {
                ResultListView.ItemsSource = new List<string> { "请先选择捕获窗口" };
                return;
            }

            // 检查引擎状态
            if (engineType == "Paddle" && !_isPaddleOcrInitialized)
            {
                ResultListView.ItemsSource = new List<string> { "PaddleOCR引擎未初始化" };
                return;
            }
            else if (engineType == "Windows" && _ocrEngine == null)
            {
                ResultListView.ItemsSource = new List<string> { "Windows OCR引擎未初始化" };
                return;
            }

            try
            {
                // 用 Win2D 获取 Direct3D 设备
                var canvasDevice = CanvasDevice.GetSharedDevice();
                if (canvasDevice is not IDirect3DDevice d3dDevice)
                {
                    ResultListView.ItemsSource = new List<string> { "无法获取 Direct3D 设备" };
                    return;
                }

                var framePool = Direct3D11CaptureFramePool.Create(
                            d3dDevice,
                            DirectXPixelFormat.B8G8R8A8UIntNormalized,
                            1,
                            _captureItem.Size);
                var session = framePool.CreateCaptureSession(_captureItem);

                session.StartCapture();
                await System.Threading.Tasks.Task.Delay(100); // 等待捕获开始

                var capturedFrame = framePool.TryGetNextFrame();
                if (capturedFrame == null)
                {
                    await System.Threading.Tasks.Task.Delay(500); // 重试
                    capturedFrame = framePool.TryGetNextFrame();
                }

                if (capturedFrame == null)
                {
                    ResultListView.ItemsSource = new List<string> { "捕获失败：无法获取帧" };
                    session.Dispose();
                    framePool.Dispose();
                    return;
                }

                // 清理上一次的位图资源
                _lastCapturedBitmap?.Dispose();
                _lastCapturedBitmap = null;

                try
                {
                    using var canvasBitmap = CanvasBitmap.CreateFromDirect3D11Surface(canvasDevice, capturedFrame.Surface);
                    var pixelBytes = canvasBitmap.GetPixelBytes();
                    _lastCapturedBitmap = new SoftwareBitmap(
                        BitmapPixelFormat.Bgra8,
                        (int)canvasBitmap.SizeInPixels.Width,
                        (int)canvasBitmap.SizeInPixels.Height,
                        BitmapAlphaMode.Premultiplied);
                    _lastCapturedBitmap.CopyFromBuffer(pixelBytes.AsBuffer());
                }
                catch (Exception ex)
                {
                    ResultListView.ItemsSource = new List<string> { $"转换位图失败: {ex.Message}" };
                    capturedFrame.Dispose();
                    session.Dispose();
                    framePool.Dispose();
                    return;
                }
                finally
                {
                    capturedFrame.Dispose(); // 确保 capturedFrame 被释放
                    session.Dispose();
                    framePool.Dispose();
                }

                if (_lastCapturedBitmap == null)
                {
                    ResultListView.ItemsSource = new List<string> { "位图转换失败" };
                    return;
                }

                // 根据选择的引擎执行OCR
                List<string> results;
                if (engineType == "Paddle")
                {
                    EngineStatusText.Text = "PaddleOCR识别中...";
                    EngineStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange);

                    results = await PaddleOcrHelper.RecognizeTextAsync(_lastCapturedBitmap);

                    EngineStatusText.Text = "PaddleOCR就绪";
                    EngineStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green);
                }
                else
                {
                    EngineStatusText.Text = "Windows OCR识别中...";
                    EngineStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange);

                    var result = await _ocrEngine!.RecognizeAsync(_lastCapturedBitmap);
                    if (result.Lines != null && result.Lines.Any())
                    {
                        results = await OcrTextHelper.GroupLinesIntoParagraphs(result.Lines);
                    }
                    else
                    {
                        results = new List<string> { "未识别到文本" };
                    }

                    EngineStatusText.Text = "Windows OCR就绪";
                    EngineStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green);
                }

                ResultListView.ItemsSource = results;
            }
            catch (Exception ex)
            {
                ResultListView.ItemsSource = new List<string> { $"发生错误: {ex.Message}" };
                EngineStatusText.Text = "OCR失败";
                EngineStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
            }
        }

        private void ResultListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var clickedItem = e.ClickedItem as string;
            if (!string.IsNullOrEmpty(clickedItem))
            {
                var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dataPackage.SetText(clickedItem);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
                // 可以选择添加一个提示，告知用户文本已复制
                // 例如：ShowCopiedNotification();
            }
        }

        // 移除了 CaptureOcrButton_Click 方法，因为功能已拆分
    }
}
