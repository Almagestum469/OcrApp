using System;
using System.Collections.Generic;
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
        private TranslationOverlay? _translationOverlay;

        private Windows.Graphics.RectInt32? _selectedRegion;
        private bool _useSelectedRegion;
        private Direct3D11CaptureFramePool? _framePool;
        private GraphicsCaptureSession? _captureSession;
        private IDirect3DDevice? _d3dDevice;

        private TaskCompletionSource<Direct3D11CaptureFrame?>? _frameCompletionSource;

        private OcrTaskPipeline? _taskPipeline;

        private bool _isAutoModeEnabled = false;
        private CancellationTokenSource? _autoModeCancellationTokenSource;
        private byte[]? _lastImageData;
        private readonly IImageHash _hashAlgorithm = new PerceptualHash();

        public MainWindow()
        {
            InitializeComponent();
            _ocrEngine = new PaddleOcrEngine();
            InitializePaddleEngineAsync();

            InitializeTaskPipeline();

            Closed += MainWindow_Closed;
        }

        private async void InitializePaddleEngineAsync()
        {
            EngineStatusText.Text = "正在初始化PaddleOCR...";
            EngineStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange);

            var success = await _ocrEngine!.InitializeAsync();
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
            StopAutoMode();

            _taskPipeline?.Dispose();

            _captureSession?.Dispose();
            _framePool?.Dispose();
            _lastCapturedBitmap?.Dispose();
            _d3dDevice = null;
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
                SelectRegionButton.IsEnabled = true;

                CreatePersistentCaptureSession();
            }
            else
            {
                ResultListView.ItemsSource = new List<string> { "未选择捕获源" };
                RecognizeButton.IsEnabled = false;
                SelectRegionButton.IsEnabled = false;

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
                        if (region.Width > 0 && region.Height > 0 &&
                            region.X >= 0 && region.Y >= 0 &&
                            region.X + region.Width <= currentBitmap.PixelWidth &&
                            region.Y + region.Height <= currentBitmap.PixelHeight)
                        {
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
                    capturedFrame.Dispose();
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
                    capturedFrame.Dispose();
                }

                if (previewBitmap == null)
                {
                    ResultListView.ItemsSource = new List<string> { "位图转换失败" };
                    return;
                }

                var regionSelector = new RegionSelector();
                regionSelector.SetCapturedBitmap(previewBitmap);
                regionSelector.SelectionConfirmed += (sender, region) =>
                {
                    if (region != null)
                    {
                        _selectedRegion = region;
                        _useSelectedRegion = true; DispatcherQueue.TryEnqueue(async () =>
                        {
                            ResultListView.ItemsSource = new List<string> { $"已设置识别区域: X={region.Value.X}, Y={region.Value.Y}, 宽={region.Value.Width}, 高={region.Value.Height}" };

                            AutoModeToggle.IsEnabled = true;

                            if (_translationOverlay == null)
                            {
                                ToggleTranslationOverlayButton_Click(ToggleTranslationOverlayButton, new RoutedEventArgs());
                            }

                            await Task.Delay(100);

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
                            AutoModeToggle.IsEnabled = false;
                            if (_isAutoModeEnabled)
                            {
                                AutoModeToggle.IsOn = false;
                            }
                        });
                    }
                };

                regionSelector.Activate();
            }
            catch (Exception ex)
            {
                ResultListView.ItemsSource = new List<string> { $"设置识别区域时出错: {ex.Message}" };
                CreatePersistentCaptureSession();
            }
        }

        private void CreatePersistentCaptureSession()
        {
            if (_captureItem == null) return;

            try
            {
                _captureSession?.Dispose();
                _framePool?.Dispose();

                var canvasDevice = CanvasDevice.GetSharedDevice();
                if (canvasDevice is not IDirect3DDevice d3dDevice)
                {
                    return;
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
                    _captureSession.IsCursorCaptureEnabled = false;
                }
                catch
                {
                }

                _captureSession.StartCapture();
            }
            catch (Exception)
            {
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

            Direct3D11CaptureFrame? latestFrame = null;
            Direct3D11CaptureFrame? currentFrame;

            while ((currentFrame = _framePool.TryGetNextFrame()) != null)
            {
                latestFrame?.Dispose();
                latestFrame = currentFrame;
            }

            if (latestFrame != null)
                return latestFrame;

            _frameCompletionSource = new TaskCompletionSource<Direct3D11CaptureFrame?>();

            try
            {
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

        private void SetOcrStatus(string status, Windows.UI.Color color)
        {
            EngineStatusText.Text = status;
            EngineStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);

            _translationOverlay?.UpdateRecognitionStatus(status);
        }

        private void ShowError(string message)
        {
            ResultListView.ItemsSource = new List<string> { message };
        }

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

        private async Task PerformOcrRecognitionAsync()
        {
            if (_lastCapturedBitmap == null || _ocrEngine == null)
                return;

            const string statusMessage = "PaddleOCR识别中...";
            SetOcrStatus(statusMessage, Microsoft.UI.Colors.Orange);

            var results = await _ocrEngine.RecognizeAsync(_lastCapturedBitmap);

            SetOcrStatus("PaddleOCR就绪", Microsoft.UI.Colors.Green);
            ResultListView.ItemsSource = results;

            if (_translationOverlay != null && results != null && results.Count > 0)
            {
                _translationOverlay.UpdateWithOcrResults(results);
            }
        }

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

        private void StartAutoMode()
        {
            if (_isAutoModeEnabled) return;

            _isAutoModeEnabled = true;
            _autoModeCancellationTokenSource = new CancellationTokenSource();
            _lastImageData = null;

            _ = Task.Run(async () => await AutoModeLoopAsync(_autoModeCancellationTokenSource.Token));
        }

        private void StopAutoMode()
        {
            if (!_isAutoModeEnabled) return;

            _isAutoModeEnabled = false;
            _autoModeCancellationTokenSource?.Cancel();
            _autoModeCancellationTokenSource?.Dispose();
            _autoModeCancellationTokenSource = null;
            _lastImageData = null;
        }

        private async Task AutoModeLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
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
                    await Task.Delay(500, cancellationToken);

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
                            similarityTask.SetResult(true);
                        }
                    });
                    var shouldRecognize = await similarityTask.Task;

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
            }
            catch (Exception)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    AutoModeToggle.IsOn = false;
                });
            }
        }

        private async Task CaptureAndRecognizeAsync()
        {
            if (_captureItem == null || _ocrEngine == null) return;

            if (!EnsureCaptureSession()) return;

            var capturedFrame = await GetLatestFrameAsync();
            if (capturedFrame == null) return;

            SoftwareBitmap? bitmap = null;
            try
            {
                bitmap = ImageProcessingService.ConvertFrameToBitmap(capturedFrame);
                if (bitmap == null) return;

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

        private async Task<bool> CheckImageSimilarityAsync()
        {
            if (_lastImageData == null) return true;

            var capturedFrame = await GetLatestFrameAsync();
            if (capturedFrame == null) return false;

            SoftwareBitmap? bitmap = null;
            try
            {
                bitmap = ImageProcessingService.ConvertFrameToBitmap(capturedFrame);
                if (bitmap == null) return false;

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

                var currentImageData = bitmap != null ? await ImageProcessingService.ConvertBitmapToPngBytesAsync(bitmap) : null;
                if (currentImageData == null) return false;

                using var lastStream = new MemoryStream(_lastImageData);
                using var currentStream = new MemoryStream(currentImageData);

                var hash1 = _hashAlgorithm.Hash(lastStream);
                var hash2 = _hashAlgorithm.Hash(currentStream);
                var similarity = CompareHash.Similarity(hash1, hash2);

                return similarity < 99.0;
            }
            catch (Exception)
            {
                return true;
            }
            finally
            {
                bitmap?.Dispose();
                capturedFrame.Dispose();
            }
        }
    }
}
