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
using OcrApp.Tasks;
using OcrApp.Services;
using Windows.Storage.Streams;
using System.Threading;
using CoenM.ImageHash;
using CoenM.ImageHash.HashAlgorithms;
using System.IO;

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

        private OcrTaskPipeline? _taskPipeline;

        // 自动模式相关变量
        private bool _isAutoModeEnabled = false;
        private CancellationTokenSource? _autoModeCancellationTokenSource;
        private byte[]? _lastImageData;
        private readonly IImageHash _hashAlgorithm = new PerceptualHash();
        public MainWindow()
        {
            InitializeComponent();
            OcrEngineComboBox.SelectionChanged += OcrEngineComboBox_SelectionChanged;
            // 默认初始化Paddle OCR
            _ocrEngine = new PaddleOcrEngine();
            _ocrEngine.InitializeAsync();
            SetDefaultOcrEngineSelection(); // 新增调用

            InitializeTaskPipeline();

            Closed += MainWindow_Closed;
        }

        private void InitializeTaskPipeline()
        {
            _taskPipeline?.Dispose();
            _taskPipeline = new OcrTaskPipeline(
                () => _ocrEngine,
                TranslateTextsAsync);

            _taskPipeline.TaskUpdated += TaskPipeline_TaskUpdated;
            _taskPipeline.PipelineError += TaskPipeline_PipelineError;
        }

        private async Task<IReadOnlyList<string>> TranslateTextsAsync(IReadOnlyList<string> texts)
        {
            var translations = new List<string>();

            foreach (var text in texts)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var translated = await GoogleTranslator.TranslateEnglishToChineseAsync(text);
                translations.Add(translated);
            }

            return translations;
        }

        private void TaskPipeline_TaskUpdated(OcrTask task)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (task.HasError)
                {
                    ShowError(task.Error ?? "任务失败");
                    SetOcrStatus("OCR失败", Microsoft.UI.Colors.Red);
                    return;
                }

                if (task.IsOcrDone && task.OcrTexts != null)
                {
                    ResultListView.ItemsSource = task.OcrTexts;
                    SetOcrStatus("OCR完成", Microsoft.UI.Colors.Green);

                    // 翻译窗口可在翻译完成后使用 translations 更新
                }

                if (task.IsTranslated && task.Translations != null)
                {
                    _translationOverlay?.UpdateWithTranslations(task.Translations);
                }
            });
        }

        private void TaskPipeline_PipelineError(Exception ex)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
            });
        }

        private void EnqueueBitmapForProcessing(SoftwareBitmap bitmap)
        {
            if (_taskPipeline == null)
            {
                ShowError("任务管线未就绪");
                return;
            }

            var task = new OcrTask(bitmap);
            _taskPipeline.Enqueue(task);
            SetOcrStatus("队列中...", Microsoft.UI.Colors.Orange);
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            // 停止自动模式
            StopAutoMode();

            _taskPipeline?.Dispose();

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

                SoftwareBitmap? currentBitmap = null;
                try
                {
                    currentBitmap = ImageProcessingService.ConvertFrameToBitmap(capturedFrame);
                    if (currentBitmap != null && _useSelectedRegion && _selectedRegion.HasValue)
                    {
                        var region = _selectedRegion.Value;
                        // 确保区域有效
                        if (region.Width > 0 && region.Height > 0 &&
                            region.X >= 0 && region.Y >= 0 &&
                            region.X + region.Width <= currentBitmap.PixelWidth &&
                            region.Y + region.Height <= currentBitmap.PixelHeight)
                        {
                            // 裁剪图像
                            currentBitmap = await ImageProcessingService.CropBitmapAsync(currentBitmap, region);
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

                if (currentBitmap == null)
                {
                    ResultListView.ItemsSource = new List<string> { "位图转换失败" };
                    return;
                }

                EnqueueBitmapForProcessing(currentBitmap);
            }
            catch (Exception ex)
            {
                ShowError($"发生错误: {ex.Message}");
                SetOcrStatus("OCR失败", Microsoft.UI.Colors.Red);

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
                    previewBitmap = ImageProcessingService.ConvertFrameToBitmap(capturedFrame);
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
                        _useSelectedRegion = true; DispatcherQueue.TryEnqueue(async () =>
                        {
                            ResultListView.ItemsSource = new List<string> { $"已设置识别区域: X={region.Value.X}, Y={region.Value.Y}, 宽={region.Value.Width}, 高={region.Value.Height}" };

                            // 启用自动模式开关
                            AutoModeToggle.IsEnabled = true;

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
                            // 禁用自动模式开关并停止自动模式
                            AutoModeToggle.IsEnabled = false;
                            if (_isAutoModeEnabled)
                            {
                                AutoModeToggle.IsOn = false;
                            }
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
                CreatePersistentCaptureSession();
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
            }
            catch (Exception)
            {
                // 清理失败的资源
                _captureSession?.Dispose();
                _framePool?.Dispose();
                _captureSession = null;
                _framePool = null;
            }
        }
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
        }
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
        }

        // 帮助方法：设置OCR状态
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
            }
            else
            {
                statusMessage = "Windows OCR识别中...";
                SetOcrStatus(statusMessage, Microsoft.UI.Colors.Orange);
            }

            var results = await _ocrEngine.RecognizeAsync(_lastCapturedBitmap);

            statusMessage = _currentEngineType == "Paddle" ? "PaddleOCR就绪" : "Windows OCR就绪";
            SetOcrStatus(statusMessage, Microsoft.UI.Colors.Green);
            ResultListView.ItemsSource = results;

            // 如果翻译窗口打开，自动更新翻译结果
            if (_translationOverlay != null && results != null && results.Count > 0)
            {
                _translationOverlay.UpdateWithOcrResults(results);
            }
        }

        // 自动模式切换事件处理器
        private void AutoModeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            var toggle = sender as ToggleSwitch;
            if (toggle == null) return;

            if (toggle.IsOn)
            {
                StartAutoMode();
            }
            else
            {
                StopAutoMode();
            }
        }

        // 启动自动模式
        private void StartAutoMode()
        {
            if (_isAutoModeEnabled) return;

            _isAutoModeEnabled = true;
            _autoModeCancellationTokenSource = new CancellationTokenSource();
            _lastImageData = null; // 重置上次图像数据

            // 启动自动模式协程
            _ = Task.Run(async () => await AutoModeLoopAsync(_autoModeCancellationTokenSource.Token));
        }

        // 停止自动模式
        private void StopAutoMode()
        {
            if (!_isAutoModeEnabled) return;

            _isAutoModeEnabled = false;
            _autoModeCancellationTokenSource?.Cancel();
            _autoModeCancellationTokenSource?.Dispose();
            _autoModeCancellationTokenSource = null;
            _lastImageData = null;
        }        // 自动模式主循环
        private async Task AutoModeLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                // 第一次启动时执行识别
                var firstRecognitionTask = new TaskCompletionSource<bool>();
                DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        await CaptureAndRecognizeAsync();
                        firstRecognitionTask.SetResult(true);
                    }
                    catch (Exception)
                    {
                        firstRecognitionTask.SetResult(false);
                    }
                });
                await firstRecognitionTask.Task;

                while (!cancellationToken.IsCancellationRequested)
                {
                    // 等待500ms
                    await Task.Delay(500, cancellationToken);

                    // 检查图像相似度
                    var similarityTask = new TaskCompletionSource<bool>();
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        try
                        {
                            var shouldRecognize = await CheckImageSimilarityAsync();
                            similarityTask.SetResult(shouldRecognize);
                        }
                        catch (Exception)
                        {
                            similarityTask.SetResult(true); // 出错时继续识别
                        }
                    });
                    var shouldRecognize = await similarityTask.Task;

                    // 只有当需要识别时才执行识别
                    if (shouldRecognize)
                    {
                        var recognitionTask = new TaskCompletionSource<bool>();
                        DispatcherQueue.TryEnqueue(async () =>
                        {
                            try
                            {
                                await CaptureAndRecognizeAsync();
                                recognitionTask.SetResult(true);
                            }
                            catch (Exception)
                            {
                                recognitionTask.SetResult(false);
                            }
                        });
                        await recognitionTask.Task;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消，不需要处理
            }
            catch (Exception)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    // 自动停止自动模式
                    AutoModeToggle.IsOn = false;
                });
            }
        }

        // 捕获并识别
        private async Task CaptureAndRecognizeAsync()
        {
            if (_captureItem == null || _ocrEngine == null) return;

            // 确保捕获会话有效
            if (!EnsureCaptureSession()) return;

            // 获取最新的捕获帧
            var capturedFrame = await GetLatestFrameAsync();
            if (capturedFrame == null) return;

            SoftwareBitmap? bitmap = null;
            try
            {
                bitmap = ImageProcessingService.ConvertFrameToBitmap(capturedFrame);
                if (bitmap == null) return;

                // 应用区域裁剪
                if (_useSelectedRegion && _selectedRegion.HasValue)
                {
                    var region = _selectedRegion.Value;
                    if (region.Width > 0 && region.Height > 0 &&
                        region.X >= 0 && region.Y >= 0 &&
                        region.X + region.Width <= bitmap.PixelWidth &&
                        region.Y + region.Height <= bitmap.PixelHeight)
                    {
                        bitmap = await ImageProcessingService.CropBitmapAsync(bitmap, region);
                    }
                }

                if (bitmap != null)
                {
                    _lastImageData = await ImageProcessingService.ConvertBitmapToPngBytesAsync(bitmap);
                    EnqueueBitmapForProcessing(bitmap);
                }
            }
            catch (Exception)
            {
                bitmap?.Dispose();
            }
            finally
            {
                capturedFrame.Dispose();
            }
        }

        // 检查图像相似度
        private async Task<bool> CheckImageSimilarityAsync()
        {
            if (_lastImageData == null) return true; // 没有上次数据，继续识别

            // 获取新的截图
            var capturedFrame = await GetLatestFrameAsync();
            if (capturedFrame == null) return false;

            SoftwareBitmap? bitmap = null;
            try
            {
                bitmap = ImageProcessingService.ConvertFrameToBitmap(capturedFrame);
                if (bitmap == null) return false;

                // 应用区域裁剪
                if (_useSelectedRegion && _selectedRegion.HasValue)
                {
                    var region = _selectedRegion.Value;
                    if (region.Width > 0 && region.Height > 0 &&
                        region.X >= 0 && region.Y >= 0 &&
                        region.X + region.Width <= bitmap.PixelWidth &&
                        region.Y + region.Height <= bitmap.PixelHeight)
                    {
                        bitmap = await ImageProcessingService.CropBitmapAsync(bitmap, region);
                    }
                }                // 转换为PNG字节数组
                var currentImageData = bitmap != null ? await ImageProcessingService.ConvertBitmapToPngBytesAsync(bitmap) : null;
                if (currentImageData == null) return false;

                // 计算图像哈希并比较相似度
                using var lastStream = new MemoryStream(_lastImageData);
                using var currentStream = new MemoryStream(currentImageData);

                var hash1 = _hashAlgorithm.Hash(lastStream);
                var hash2 = _hashAlgorithm.Hash(currentStream); var similarity = CompareHash.Similarity(hash1, hash2);

                // 如果相似度小于95%，则需要重新识别
                return similarity < 99.0;
            }
            catch (Exception)
            {
                return true; // 出错时继续识别
            }
            finally
            {
                bitmap?.Dispose();
                capturedFrame.Dispose();
            }
        }
    }
}
