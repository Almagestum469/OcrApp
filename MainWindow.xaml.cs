using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Microsoft.Graphics.Canvas;
using OcrApp.Engines;

namespace OcrApp
{
    public sealed partial class MainWindow : Window
    {
        private GraphicsCaptureItem? _captureItem;
        private SoftwareBitmap? _lastCapturedBitmap;
        private IOcrEngine? _ocrEngine; // 统一接口
        private string _currentEngineType = "Paddle";
        private TranslationOverlay? _translationOverlay;

        // P/Invoke declarations for global keyboard hook
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104; // For F-keys when Alt is pressed, though F2 alone is usually WM_KEYDOWN

        private static LowLevelKeyboardProc _proc = HookCallback; // Keep a reference to prevent GC
        private static IntPtr _hookID = IntPtr.Zero;

        public MainWindow()
        {
            InitializeComponent();
            OcrEngineComboBox.SelectionChanged += OcrEngineComboBox_SelectionChanged;
            // 默认初始化Paddle OCR
            _ocrEngine = new PaddleOcrEngine();
            _ocrEngine.InitializeAsync();
            SetDefaultOcrEngineSelection(); // 新增调用

            _hookID = SetHook(_proc);
            this.Closed += MainWindow_Closed;
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            UnhookWindowsHookEx(_hookID);
        }

        private static IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using (var curProcess = System.Diagnostics.Process.GetCurrentProcess())
            using (var curModule = curProcess.MainModule)
            {
                if (curModule != null) // Add null check for curModule
                {
                    return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
                }
                return IntPtr.Zero; // Or handle error appropriately
            }
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                int vkCode = Marshal.ReadInt32(lParam);
                // Check for F2 key (VK_F2 = 0x71)
                if (vkCode == 0x71)
                {
                    // Get the current MainWindow instance if it exists
                    var currentWindow = App.CurrentWindow as MainWindow;
                    if (currentWindow != null && currentWindow.RecognizeButton.IsEnabled)
                    {
                        // Ensure the click is dispatched to the UI thread
                        currentWindow.DispatcherQueue.TryEnqueue(() =>
                        {
                            currentWindow.RecognizeButton_Click(currentWindow.RecognizeButton, new RoutedEventArgs());
                        });
                        return (IntPtr)1; // Indicate that the event was handled
                    }
                }
            }
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        private void SetDefaultOcrEngineSelection()
        {
            if (OcrEngineComboBox.Items.Count > 0)
            {
                for (int i = 0; i < OcrEngineComboBox.Items.Count; i++)
                {
                    if (OcrEngineComboBox.Items[i] is ComboBoxItem item && item.Tag?.ToString() == _currentEngineType)
                    {
                        OcrEngineComboBox.SelectedIndex = i;
                        break;
                    }
                }
            }
        }

        private void UpdateDebugInfo(string debugInfo)
        {
            DebugTextBlock.Text = debugInfo;
            DebugScrollViewer.UpdateLayout();
            DebugScrollViewer.ScrollToVerticalOffset(DebugScrollViewer.ScrollableHeight);
        }
        private async void OcrEngineComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selectedItem = OcrEngineComboBox.SelectedItem as ComboBoxItem;
            var engineType = selectedItem?.Tag?.ToString();
            _currentEngineType = engineType ?? "Paddle";

            if (engineType == "Paddle")
            {
                EngineStatusText.Text = "正在初始化PaddleOCR...";
                EngineStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange);
                _ocrEngine = new PaddleOcrEngine();
                var success = await _ocrEngine.InitializeAsync();
                if (success)
                {
                    EngineStatusText.Text = "PaddleOCR就绪";
                    EngineStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green);
                }
                else
                {
                    EngineStatusText.Text = "PaddleOCR初始化失败";
                    EngineStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
                }
            }
            else
            {
                EngineStatusText.Text = "Windows OCR就绪";
                EngineStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green);
                _ocrEngine = new WindowsOcrEngine();
                await _ocrEngine.InitializeAsync();
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
            if (_captureItem == null)
            {
                ResultListView.ItemsSource = new List<string> { "请先选择捕获窗口" };
                return;
            }
            if (_ocrEngine == null)
            {
                ResultListView.ItemsSource = new List<string> { "OCR引擎未初始化" };
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
                session.IsCursorCaptureEnabled = false; // 禁用鼠标光标捕获
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
                // 统一通过接口调用OCR
                string statusMessage;
                if (_currentEngineType == "Paddle")
                {
                    statusMessage = "PaddleOCR识别中...";
                    EngineStatusText.Text = statusMessage;
                    EngineStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange);
                    UpdateDebugInfo("开始使用PaddleOCR识别...");

                    // 同步状态到翻译窗口
                    if (_translationOverlay != null)
                    {
                        _translationOverlay.UpdateRecognitionStatus(statusMessage);
                    }
                }
                else
                {
                    statusMessage = "Windows OCR识别中...";
                    EngineStatusText.Text = statusMessage;
                    EngineStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange);
                    UpdateDebugInfo("开始使用Windows OCR识别...");

                    // 同步状态到翻译窗口
                    if (_translationOverlay != null)
                    {
                        _translationOverlay.UpdateRecognitionStatus(statusMessage);
                    }
                }

                var results = await _ocrEngine.RecognizeAsync(_lastCapturedBitmap);
                var debugInfo = _ocrEngine.GenerateDebugInfo();
                UpdateDebugInfo(debugInfo);

                statusMessage = _currentEngineType == "Paddle" ? "PaddleOCR就绪" : "Windows OCR就绪";
                EngineStatusText.Text = statusMessage;
                EngineStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green);
                ResultListView.ItemsSource = results;

                // 如果翻译窗口打开，自动更新翻译结果
                if (_translationOverlay != null && results != null && results.Count > 0)
                {
                    _translationOverlay.UpdateWithOcrResults(results);
                }
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
            }
        }

        private void ToggleTranslationOverlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_translationOverlay == null)
            {
                // 创建新的翻译窗口
                _translationOverlay = new TranslationOverlay();
                _translationOverlay.Closed += (s, args) =>
                {
                    _translationOverlay = null;
                    ToggleTranslationOverlayButton.Content = "翻译窗口";
                };
                _translationOverlay.Activate();
                ToggleTranslationOverlayButton.Content = "关闭翻译窗口";
            }
            else
            {
                // 关闭现有窗口
                _translationOverlay.Close();
                _translationOverlay = null;
                ToggleTranslationOverlayButton.Content = "翻译窗口";
            }
        }

    }
}
