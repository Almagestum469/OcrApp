using System;
using System.Collections.Generic;
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
using OcrApp.Utils;
using Windows.Storage.Streams;

namespace OcrApp
{
    public sealed partial class MainWindow
    {
        private GraphicsCaptureItem? _captureItem;
        private SoftwareBitmap? _lastCapturedBitmap;
        private IOcrEngine? _ocrEngine;
        private string _currentEngineType = "Paddle";
        private TranslationOverlay? _translationOverlay;

        // 添加区域选择相关变量
        private Windows.Graphics.RectInt32? _selectedRegion;
        private bool _useSelectedRegion;        // 添加长时间维持的CaptureSession相关变量
        private Direct3D11CaptureFramePool? _framePool;
        private GraphicsCaptureSession? _captureSession;
        private IDirect3DDevice? _d3dDevice;

        // 事件驱动的帧获取相关变量
        private TaskCompletionSource<Direct3D11CaptureFrame?>? _frameCompletionSource;

        // 全局快捷键管理器
        private GlobalHotkeyManager? _hotkeyManager;
        public MainWindow()
        {
            InitializeComponent();
            OcrEngineComboBox.SelectionChanged += OcrEngineComboBox_SelectionChanged;
            // 默认初始化Paddle OCR
            _ocrEngine = new PaddleOcrEngine();
            _ocrEngine.InitializeAsync();
            SetDefaultOcrEngineSelection(); // 新增调用

            // 初始化快捷键管理器
            InitializeHotkeyManager();

            this.Closed += MainWindow_Closed;
        }

        private void InitializeHotkeyManager()
        {
            _hotkeyManager = new GlobalHotkeyManager();

            // 设置快捷键按钮和延迟滑块的初始值
            if (HotkeyButton != null)
            {
                HotkeyButton.Content = GlobalHotkeyManager.GetKeyNameFromVirtualKey(_hotkeyManager.TriggerHotkeyCode);
            }
            if (DelaySlider != null)
            {
                DelaySlider.Value = _hotkeyManager.TriggerDelayMs;
            }
            if (DelayValueText != null)
            {
                DelayValueText.Text = $"{_hotkeyManager.TriggerDelayMs}ms";
            }

            // 订阅快捷键事件
            _hotkeyManager.HotkeySetRequested += OnHotkeySetRequested;
            _hotkeyManager.TriggerRequested += OnTriggerRequested;
        }

        private void OnHotkeySetRequested(int vkCode)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                SetHotkey(vkCode);
            });
        }

        private void OnTriggerRequested()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (RecognizeButton.IsEnabled)
                {
                    RecognizeButton_Click(RecognizeButton, new RoutedEventArgs());
                }
            });
        }
        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            _hotkeyManager?.Dispose();

            // 清理长时间维持的CaptureSession和相关资源
            _captureSession?.Dispose();
            _framePool?.Dispose();
            _lastCapturedBitmap?.Dispose();
            _d3dDevice = null;
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
                _lastCapturedBitmap = null; try
                {
                    _lastCapturedBitmap = ConvertFrameToBitmap(capturedFrame); if (_lastCapturedBitmap != null && _useSelectedRegion && _selectedRegion.HasValue)
                    {
                        var region = _selectedRegion.Value;
                        // 确保区域有效
                        if (region.Width > 0 && region.Height > 0 &&
                            region.X >= 0 && region.Y >= 0 &&
                            region.X + region.Width <= _lastCapturedBitmap.PixelWidth &&
                            region.Y + region.Height <= _lastCapturedBitmap.PixelHeight)
                        {
                            // 裁剪图像
                            _lastCapturedBitmap = await CropBitmapAsync(_lastCapturedBitmap, region);
                        }
                        else
                        {
                            // 区域无效，使用全图
                            UpdateDebugInfo("选定区域无效，使用全图");
                        }
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
                }                // 执行OCR识别
                await PerformOcrRecognitionAsync();
            }
            catch (Exception ex)
            {
                ShowError($"发生错误: {ex.Message}");
                SetOcrStatus("OCR失败", Microsoft.UI.Colors.Red);

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
                    previewBitmap = ConvertFrameToBitmap(capturedFrame);
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
            if (_hotkeyManager != null)
            {
                // 设置新的热键
                _hotkeyManager.TriggerHotkeyCode = vkCode;
                _hotkeyManager.IsSettingHotkey = false;

                // 更新按钮显示
                HotkeyButton.Content = GlobalHotkeyManager.GetKeyNameFromVirtualKey(vkCode);
                HotkeyButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LightGray);
            }
        }

        private void HotkeyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_hotkeyManager == null) return;

            if (_hotkeyManager.IsSettingHotkey)
            {
                // 如果已经处于设置状态，则取消设置
                _hotkeyManager.IsSettingHotkey = false;
                HotkeyButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LightGray);
            }
            else
            {
                // 进入设置状态
                _hotkeyManager.IsSettingHotkey = true;
                HotkeyButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange);
                HotkeyButton.Content = "按下按键...";
            }
        }

        private void DelaySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_hotkeyManager != null)
            {
                _hotkeyManager.TriggerDelayMs = (int)e.NewValue;
                if (DelayValueText != null)
                {
                    DelayValueText.Text = $"{_hotkeyManager.TriggerDelayMs}ms";
                }
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
                    1,
                    _captureItem.Size);

                // 订阅帧到达事件
                _framePool.FrameArrived += OnFrameArrived;

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
                UpdateDebugInfo("已创建持久化捕获会话（事件驱动模式）");
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
        }        /// <summary>
                 /// 获取最新的捕获帧
                 /// </summary>
                 /// <returns>最新的捕获帧，如果获取失败返回null</returns>
        private async Task<Direct3D11CaptureFrame?> GetLatestFrameAsync()
        {
            if (_framePool == null)
                return null;

            // 先尝试获取立即可用的帧
            Direct3D11CaptureFrame? latestFrame = null;
            Direct3D11CaptureFrame? currentFrame;

            while ((currentFrame = _framePool.TryGetNextFrame()) != null)
            {
                latestFrame?.Dispose(); // 释放之前的帧
                latestFrame = currentFrame; // 保留当前帧作为最新帧
            }

            if (latestFrame != null)
                return latestFrame;

            // 如果没有立即可用的帧，等待新帧
            _frameCompletionSource = new TaskCompletionSource<Direct3D11CaptureFrame?>();

            try
            {
                // 设置超时
                using var cts = new System.Threading.CancellationTokenSource(1000);
                cts.Token.Register(() => _frameCompletionSource?.TrySetResult(null));

                return await _frameCompletionSource.Task;
            }
            finally
            {
                _frameCompletionSource = null;
            }
        }/// <summary>
         /// 帧到达事件处理器
         /// </summary>
        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            // 如果有等待中的请求，立即满足它
            if (_frameCompletionSource != null)
            {
                var frame = sender.TryGetNextFrame();
                _frameCompletionSource.SetResult(frame);
                _frameCompletionSource = null;
            }
            else
            {
                // 如果没有等待请求，清空帧池避免积累旧帧
                Direct3D11CaptureFrame? frame;
                while ((frame = sender.TryGetNextFrame()) != null)
                {
                    frame.Dispose(); // 释放不需要的帧
                }
            }
        }        // 帮助方法：从捕获帧转换为SoftwareBitmap
        private SoftwareBitmap? ConvertFrameToBitmap(Direct3D11CaptureFrame capturedFrame)
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
            catch (Exception ex)
            {
                UpdateDebugInfo($"位图转换失败: {ex.Message}");
                return null;
            }
        }

        // 帮助方法：裁剪位图
        private async Task<SoftwareBitmap?> CropBitmapAsync(SoftwareBitmap sourceBitmap, Windows.Graphics.RectInt32 region)
        {
            try
            {
                // 验证区域有效性
                if (region.Width <= 0 || region.Height <= 0 ||
                    region.X < 0 || region.Y < 0 ||
                    region.X + region.Width > sourceBitmap.PixelWidth ||
                    region.Y + region.Height > sourceBitmap.PixelHeight)
                {
                    UpdateDebugInfo("选定区域无效，使用全图");
                    return sourceBitmap;
                }

                using var ms = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ms);
                encoder.SetSoftwareBitmap(sourceBitmap);
                encoder.BitmapTransform.Bounds = new Windows.Graphics.Imaging.BitmapBounds
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

                UpdateDebugInfo($"已应用区域裁剪: X={region.X}, Y={region.Y}, 宽={region.Width}, 高={region.Height}");
                return croppedBitmap;
            }
            catch (Exception ex)
            {
                UpdateDebugInfo($"裁剪失败: {ex.Message}");
                return sourceBitmap; // 返回原图
            }
        }        // 帮助方法：设置OCR状态
        private void SetOcrStatus(string status, Windows.UI.Color color)
        {
            EngineStatusText.Text = status;
            EngineStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);

            // 同步状态到翻译窗口
            _translationOverlay?.UpdateRecognitionStatus(status);
        }

        // 帮助方法：显示错误信息
        private void ShowError(string message)
        {
            ResultListView.ItemsSource = new List<string> { message };
        }

        // 帮助方法：确保捕获会话有效
        private bool EnsureCaptureSession()
        {
            if (_framePool != null && _captureSession != null)
                return true;

            CreatePersistentCaptureSession();

            if (_framePool == null || _captureSession == null)
            {
                ShowError("捕获会话创建失败");
                return false;
            }

            return true;
        }

        // 帮助方法：执行OCR识别
        private async Task PerformOcrRecognitionAsync()
        {
            if (_lastCapturedBitmap == null || _ocrEngine == null)
                return;

            string statusMessage;
            if (_currentEngineType == "Paddle")
            {
                statusMessage = "PaddleOCR识别中...";
                SetOcrStatus(statusMessage, Microsoft.UI.Colors.Orange);
                UpdateDebugInfo("开始使用PaddleOCR识别...");
            }
            else
            {
                statusMessage = "Windows OCR识别中...";
                SetOcrStatus(statusMessage, Microsoft.UI.Colors.Orange);
                UpdateDebugInfo("开始使用Windows OCR识别...");
            }

            var results = await _ocrEngine.RecognizeAsync(_lastCapturedBitmap);
            var debugInfo = _ocrEngine.GenerateDebugInfo();
            UpdateDebugInfo(debugInfo);

            statusMessage = _currentEngineType == "Paddle" ? "PaddleOCR就绪" : "Windows OCR就绪";
            SetOcrStatus(statusMessage, Microsoft.UI.Colors.Green);
            ResultListView.ItemsSource = results;

            // 如果翻译窗口打开，自动更新翻译结果
            if (_translationOverlay != null && results != null && results.Count > 0)
            {
                _translationOverlay.UpdateWithOcrResults(results);
            }
        }
    }
}
