using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Microsoft.Graphics.Canvas;
using OcrApp.Engines;
using Windows.Storage.Streams;

namespace OcrApp
{
    public sealed partial class MainWindow
    {
        private GraphicsCaptureItem? _captureItem;
        private SoftwareBitmap? _lastCapturedBitmap;
        private IOcrEngine? _ocrEngine; // 统一接口
        private string _currentEngineType = "Paddle";
        private TranslationOverlay? _translationOverlay;
        // 添加区域选择相关变量
        private Windows.Graphics.RectInt32? _selectedRegion;
        private bool _useSelectedRegion;

        // 添加长时间维持的CaptureSession相关变量
        private Direct3D11CaptureFramePool? _framePool;
        private GraphicsCaptureSession? _captureSession;
        private IDirect3DDevice? _d3dDevice;

        // 添加快捷键和触发延迟相关变量
        private int _triggerHotkeyCode = 0x20; // 默认空格键 (VK_SPACE = 0x20)
        private int _triggerDelayMs = 600; // 默认600毫秒延迟
        private bool _isSettingHotkey; // 是否处于设置快捷键状态
        private System.Threading.Timer? _triggerDelayTimer; // 触发延迟计时器

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
        private static IntPtr _hookID = IntPtr.Zero; public MainWindow()
        {
            InitializeComponent();
            OcrEngineComboBox.SelectionChanged += OcrEngineComboBox_SelectionChanged;
            // 默认初始化Paddle OCR
            _ocrEngine = new PaddleOcrEngine();
            _ocrEngine.InitializeAsync();
            SetDefaultOcrEngineSelection(); // 新增调用            // 设置快捷键按钮和延迟滑块的初始值
            if (HotkeyButton != null)
            {
                HotkeyButton.Content = GetKeyNameFromVirtualKey(_triggerHotkeyCode);
            }
            if (DelaySlider != null)
            {
                DelaySlider.Value = _triggerDelayMs;
            }
            if (DelayValueText != null)
            {
                DelayValueText.Text = $"{_triggerDelayMs}ms";
            }

            _hookID = SetHook(_proc);
            this.Closed += MainWindow_Closed;
        }
        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            UnhookWindowsHookEx(_hookID);
            _triggerDelayTimer?.Dispose();

            // 清理长时间维持的CaptureSession和相关资源
            _captureSession?.Dispose();
            _framePool?.Dispose();
            _lastCapturedBitmap?.Dispose();
            _d3dDevice = null;
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

                // 获取当前MainWindow实例
                var currentWindow = App.CurrentWindow as MainWindow;

                if (currentWindow != null)
                {
                    // 如果正在设置快捷键，则捕获当前按键并设置为新的快捷键
                    if (currentWindow._isSettingHotkey)
                    {
                        currentWindow.DispatcherQueue.TryEnqueue(() =>
                        {
                            currentWindow.SetHotkey(vkCode);
                        });
                        // 不再传递事件，避免快捷键对应用程序产生影响
                        return (IntPtr)1;
                    }
                    // 检查是否匹配当前设置的快捷键或F2键(0x71)
                    else if ((vkCode == currentWindow._triggerHotkeyCode || vkCode == 0x71) && currentWindow.RecognizeButton.IsEnabled)
                    {
                        // 使用延迟触发
                        currentWindow.DispatcherQueue.TryEnqueue(() =>
                        {
                            // 停止之前的计时器（如果存在）
                            currentWindow._triggerDelayTimer?.Dispose();

                            // 创建新的计时器
                            currentWindow._triggerDelayTimer = new System.Threading.Timer(
                                _ => currentWindow.DispatcherQueue.TryEnqueue(() =>
                                    currentWindow.RecognizeButton_Click(currentWindow.RecognizeButton, new RoutedEventArgs())
                                ),
                                null,
                                currentWindow._triggerDelayMs,  // 等待设置的延迟时间
                                System.Threading.Timeout.Infinite // 不重复
                            );
                        });
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
                SelectRegionButton.IsEnabled = true; // 启用区域选择按钮

                // 创建持久化的捕获会话
                CreatePersistentCaptureSession();
            }
            else
            {
                ResultListView.ItemsSource = new List<string> { "未选择捕获源" };
                RecognizeButton.IsEnabled = false;
                SelectRegionButton.IsEnabled = false; // 禁用区域选择按钮

                // 清理捕获会话
                _captureSession?.Dispose();
                _framePool?.Dispose();
                _captureSession = null;
                _framePool = null;
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

            // 如果持久化会话不存在，尝试重新创建
            if (_framePool == null || _captureSession == null)
            {
                CreatePersistentCaptureSession();
                if (_framePool == null || _captureSession == null)
                {
                    ResultListView.ItemsSource = new List<string> { "捕获会话创建失败" };
                    return;
                }
            }
            try
            {
                // 获取最新的捕获帧
                var capturedFrame = await GetLatestFrameAsync();
                if (capturedFrame == null)
                {
                    ResultListView.ItemsSource = new List<string> { "捕获失败：无法获取帧" };
                    return;
                }

                // 清理上一次的位图资源
                _lastCapturedBitmap?.Dispose();
                _lastCapturedBitmap = null;

                try
                {
                    using var canvasBitmap = CanvasBitmap.CreateFromDirect3D11Surface(
                        CanvasDevice.GetSharedDevice(), capturedFrame.Surface);
                    var pixelBytes = canvasBitmap.GetPixelBytes();
                    SoftwareBitmap fullBitmap = new SoftwareBitmap(
                        BitmapPixelFormat.Bgra8,
                        (int)canvasBitmap.SizeInPixels.Width,
                        (int)canvasBitmap.SizeInPixels.Height,
                        BitmapAlphaMode.Premultiplied);
                    fullBitmap.CopyFromBuffer(pixelBytes.AsBuffer());

                    // 如果有选择区域，裁剪图像
                    if (_useSelectedRegion && _selectedRegion.HasValue)
                    {
                        var region = _selectedRegion.Value;
                        // 确保区域有效
                        if (region.Width > 0 && region.Height > 0 &&
                            region.X >= 0 && region.Y >= 0 &&
                            region.X + region.Width <= fullBitmap.PixelWidth &&
                            region.Y + region.Height <= fullBitmap.PixelHeight)
                        {
                            // 裁剪图像
                            _lastCapturedBitmap = new SoftwareBitmap(
                                fullBitmap.BitmapPixelFormat,
                                region.Width,
                                region.Height,
                                fullBitmap.BitmapAlphaMode);
                            // 使用BitmapTransform暂时不支持直接裁剪，我们将通过像素复制来实现
                            // 创建临时共享空间
                            var sourceBuffer = new Windows.Storage.Streams.Buffer((uint)(fullBitmap.PixelWidth * fullBitmap.PixelHeight * 4));
                            var destBuffer = new Windows.Storage.Streams.Buffer((uint)(region.Width * region.Height * 4));

                            // 将全图复制到缓冲区
                            fullBitmap.CopyToBuffer(sourceBuffer);

                            // 创建裁剪后图像的编码器
                            using var ms = new InMemoryRandomAccessStream();
                            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ms);

                            // 设置裁剪参数
                            encoder.SetSoftwareBitmap(fullBitmap);
                            encoder.BitmapTransform.Bounds = new Windows.Graphics.Imaging.BitmapBounds
                            {
                                X = (uint)region.X,
                                Y = (uint)region.Y,
                                Width = (uint)region.Width,
                                Height = (uint)region.Height
                            };

                            // 执行裁剪
                            await encoder.FlushAsync();

                            // 解码裁剪后的图像
                            ms.Seek(0);
                            var decoder = await BitmapDecoder.CreateAsync(ms);
                            var cropped = await decoder.GetSoftwareBitmapAsync();

                            // 保存裁剪结果
                            _lastCapturedBitmap = cropped;
                            UpdateDebugInfo($"已应用区域裁剪: X={region.X}, Y={region.Y}, 宽={region.Width}, 高={region.Height}");
                        }
                        else
                        {
                            // 区域无效，使用全图
                            _lastCapturedBitmap = fullBitmap;
                            UpdateDebugInfo("选定区域无效，使用全图");
                        }
                    }
                    else
                    {
                        // 无选择区域，使用全图
                        _lastCapturedBitmap = fullBitmap;
                    }
                }
                catch (Exception ex)
                {
                    ResultListView.ItemsSource = new List<string> { $"转换位图失败: {ex.Message}" };
                    capturedFrame.Dispose();
                    return;
                }
                finally
                {
                    capturedFrame.Dispose(); // 确保 capturedFrame 被释放
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

                // 如果出现错误，可能是会话失效，尝试重新创建
                UpdateDebugInfo($"OCR过程中出现错误，尝试重新创建捕获会话: {ex.Message}");
                CreatePersistentCaptureSession();
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
                    // 重新启用快捷键和延迟设置
                    HotkeyButton.IsEnabled = true;
                    DelaySlider.IsEnabled = true;
                };
                _translationOverlay.Activate();
                ToggleTranslationOverlayButton.Content = "关闭翻译窗口";
                // 禁用快捷键和延迟设置
                HotkeyButton.IsEnabled = false;
                DelaySlider.IsEnabled = false;
            }
            else
            {
                // 关闭现有窗口
                _translationOverlay.Close();
                _translationOverlay = null;
                ToggleTranslationOverlayButton.Content = "翻译窗口";
            }
        }
        private async void SelectRegionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_captureItem == null)
            {
                ResultListView.ItemsSource = new List<string> { "请先选择捕获窗口" };
                return;
            }

            // 如果持久化会话不存在，尝试重新创建
            if (_framePool == null || _captureSession == null)
            {
                CreatePersistentCaptureSession();
                if (_framePool == null || _captureSession == null)
                {
                    ResultListView.ItemsSource = new List<string> { "捕获会话创建失败" };
                    return;
                }
            }
            try
            {
                // 获取最新的捕获帧
                var capturedFrame = await GetLatestFrameAsync();
                if (capturedFrame == null)
                {
                    ResultListView.ItemsSource = new List<string> { "捕获失败：无法获取帧" };
                    return;
                }

                SoftwareBitmap? previewBitmap = null;
                try
                {
                    using var canvasBitmap = CanvasBitmap.CreateFromDirect3D11Surface(
                        CanvasDevice.GetSharedDevice(), capturedFrame.Surface);
                    var pixelBytes = canvasBitmap.GetPixelBytes();
                    previewBitmap = new SoftwareBitmap(
                        BitmapPixelFormat.Bgra8,
                        (int)canvasBitmap.SizeInPixels.Width,
                        (int)canvasBitmap.SizeInPixels.Height,
                        BitmapAlphaMode.Premultiplied);
                    previewBitmap.CopyFromBuffer(pixelBytes.AsBuffer());
                }
                catch (Exception ex)
                {
                    ResultListView.ItemsSource = new List<string> { $"转换位图失败: {ex.Message}" };
                    capturedFrame.Dispose();
                    return;
                }
                finally
                {
                    capturedFrame.Dispose(); // 确保 capturedFrame 被释放
                }

                if (previewBitmap == null)
                {
                    ResultListView.ItemsSource = new List<string> { "位图转换失败" };
                    return;
                }

                // 创建区域选择窗口
                var regionSelector = new RegionSelector();
                regionSelector.SetCapturedBitmap(previewBitmap);                // 订阅区域选择完成事件
                regionSelector.SelectionConfirmed += (sender, region) =>
                {
                    if (region != null)
                    {
                        _selectedRegion = region;
                        _useSelectedRegion = true;
                        DispatcherQueue.TryEnqueue(async () =>
                        {
                            ResultListView.ItemsSource = new List<string> { $"已设置识别区域: X={region.Value.X}, Y={region.Value.Y}, 宽={region.Value.Width}, 高={region.Value.Height}" };

                            // 自动打开翻译窗口（如果还没有打开）
                            if (_translationOverlay == null)
                            {
                                ToggleTranslationOverlayButton_Click(ToggleTranslationOverlayButton, new RoutedEventArgs());
                            }

                            // 等待一小段时间确保翻译窗口初始化完成
                            await Task.Delay(100);

                            // 自动执行识别
                            RecognizeButton_Click(RecognizeButton, new RoutedEventArgs());
                        });
                    }
                    else
                    {
                        _selectedRegion = null;
                        _useSelectedRegion = false;
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            ResultListView.ItemsSource = new List<string> { "未设置识别区域，将识别整个窗口" };
                        });
                    }
                };

                // 显示区域选择窗口
                regionSelector.Activate();
            }
            catch (Exception ex)
            {
                ResultListView.ItemsSource = new List<string> { $"设置识别区域时出错: {ex.Message}" };
                // 如果出现错误，可能是会话失效，尝试重新创建
                UpdateDebugInfo($"设置识别区域时出现错误，尝试重新创建捕获会话: {ex.Message}");
                CreatePersistentCaptureSession();
            }
        }

        private void SetHotkey(int vkCode)
        {
            // 设置新的热键
            _triggerHotkeyCode = vkCode;
            _isSettingHotkey = false;

            // 更新按钮显示
            HotkeyButton.Content = GetKeyNameFromVirtualKey(vkCode);
            HotkeyButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LightGray);
        }

        private string GetKeyNameFromVirtualKey(int vkCode)
        {
            // 特殊键映射
            switch (vkCode)
            {
                case 0x20: return "空格";
                case 0x1B: return "Esc";
                case 0x09: return "Tab";
                case 0x0D: return "Enter";
                case 0x08: return "Back";
                case 0x2E: return "Delete";
                case 0x25: return "←";
                case 0x26: return "↑";
                case 0x27: return "→";
                case 0x28: return "↓";
                default:
                    // 对于标准字母数字键，直接转换为字符
                    if ((vkCode >= 0x30 && vkCode <= 0x39) || // 0-9
                        (vkCode >= 0x41 && vkCode <= 0x5A))   // A-Z
                    {
                        return ((char)vkCode).ToString();
                    }

                    // F1-F12
                    if (vkCode >= 0x70 && vkCode <= 0x7B)
                    {
                        return $"F{vkCode - 0x6F}";
                    }

                    // 对于其他键，返回虚拟键码
                    return $"Key({vkCode})";
            }
        }

        private void HotkeyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isSettingHotkey)
            {
                // 如果已经处于设置状态，则取消设置
                _isSettingHotkey = false;
                HotkeyButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LightGray);
            }
            else
            {
                // 进入设置状态
                _isSettingHotkey = true;
                HotkeyButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange);
                HotkeyButton.Content = "按下按键...";
            }
        }
        private void DelaySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            _triggerDelayMs = (int)e.NewValue;
            if (DelayValueText != null) // 添加空检查
            {
                DelayValueText.Text = $"{_triggerDelayMs}ms";
            }
        }

        private void CreatePersistentCaptureSession()
        {
            if (_captureItem == null) return;

            try
            {
                // 清理之前的会话
                _captureSession?.Dispose();
                _framePool?.Dispose();

                // 获取 Direct3D 设备
                var canvasDevice = CanvasDevice.GetSharedDevice();
                if (canvasDevice is not IDirect3DDevice d3dDevice)
                {
                    UpdateDebugInfo("无法获取 Direct3D 设备");
                    return;
                }

                _d3dDevice = d3dDevice;                // 创建帧池和会话
                _framePool = Direct3D11CaptureFramePool.Create(
                    d3dDevice,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    1, // 使用单个缓冲区，减少旧帧缓存
                    _captureItem.Size);

                _captureSession = _framePool.CreateCaptureSession(_captureItem);

                try
                {
                    // 尝试禁用鼠标光标捕获(可能在某些Windows版本上不可用)
                    _captureSession.IsCursorCaptureEnabled = false;
                }
                catch
                {
                    // 忽略不支持此属性的平台错误
                }

                // 启动捕获会话
                _captureSession.StartCapture();
                UpdateDebugInfo("已创建持久化捕获会话");
            }
            catch (Exception ex)
            {
                UpdateDebugInfo($"创建持久化捕获会话失败: {ex.Message}");
                // 清理失败的资源
                _captureSession?.Dispose();
                _framePool?.Dispose();
                _captureSession = null;
                _framePool = null;
            }
        }

        /// <summary>
        /// 获取最新的捕获帧，清空旧帧缓存
        /// </summary>
        /// <returns>最新的捕获帧，如果获取失败返回null</returns>
        private async Task<Direct3D11CaptureFrame?> GetLatestFrameAsync()
        {
            if (_framePool == null)
                return null;

            // 先清空帧池中的旧帧
            Direct3D11CaptureFrame? oldFrame;
            while ((oldFrame = _framePool.TryGetNextFrame()) != null)
            {
                oldFrame.Dispose(); // 释放旧帧
            }

            // 等待新帧生成
            await System.Threading.Tasks.Task.Delay(50);

            // 尝试获取最新帧
            var capturedFrame = _framePool.TryGetNextFrame();
            if (capturedFrame == null)
            {
                await System.Threading.Tasks.Task.Delay(100); // 再等待一下
                capturedFrame = _framePool.TryGetNextFrame();
            }

            // 如果还是获取不到，尝试重新创建会话
            if (capturedFrame == null)
            {
                UpdateDebugInfo("获取帧失败，重新创建捕获会话");
                CreatePersistentCaptureSession();
                await System.Threading.Tasks.Task.Delay(200); // 等待会话启动

                // 再次尝试获取帧
                capturedFrame = _framePool?.TryGetNextFrame();
            }

            return capturedFrame;
        }
    }
}
