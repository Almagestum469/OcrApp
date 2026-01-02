using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics.Capture;
using Windows.Graphics.Imaging;
using OcrApp.Engines;
using OcrApp.Tasks;
using OcrApp.Services;
using System.Threading;

namespace OcrApp
{
    public sealed partial class MainWindow
    {
        private GraphicsCaptureItem? _captureItem;
        private IOcrEngine? _ocrEngine;
        private TranslationOverlay? _translationOverlay;

        private Windows.Graphics.RectInt32? _selectedRegion;
        private bool _useSelectedRegion;

        private OcrTaskPipeline? _taskPipeline;

        private bool _isAutoModeEnabled = false;
        private CancellationTokenSource? _autoModeCancellationTokenSource;
        private byte[]? _lastImageData;
        private readonly ScreenCaptureService _captureService = new();

        public MainWindow()
        {
            InitializeComponent();
            _ocrEngine = new PaddleOcrEngine();
            InitializePaddleEngineAsync();

            InitializeTaskPipeline();

            _captureService.CaptureFailed += (s, e) =>
            {
                DispatcherQueue.TryEnqueue(() => ShowError("捕获会话创建失败"));
            };

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
                () => _ocrEngine);

            _taskPipeline.TaskUpdated += TaskPipeline_TaskUpdated;
            _taskPipeline.PipelineError += TaskPipeline_PipelineError;
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
            _captureService.Dispose();
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

                var sessionCreated = await _captureService.InitializeAsync(_captureItem);
                if (!sessionCreated)
                {
                    ResultListView.ItemsSource = new List<string> { "捕获会话创建失败" };
                }
            }
            else
            {
                ResultListView.ItemsSource = new List<string> { "未选择捕获源" };
                RecognizeButton.IsEnabled = false;
                SelectRegionButton.IsEnabled = false;
                _captureService.Dispose();
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
                var currentBitmap = await _captureService.CaptureBitmapAsync(_selectedRegion, _useSelectedRegion);
                if (currentBitmap == null)
                {
                    ResultListView.ItemsSource = new List<string> { "捕获失败：无法获取帧" };
                    return;
                }

                EnqueueBitmapForProcessing(currentBitmap);
            }
            catch (Exception ex)
            {
                ShowError($"发生错误: {ex.Message}");
                SetOcrStatus("OCR失败", Microsoft.UI.Colors.Red);
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

            try
            {
                var previewBitmap = await _captureService.CaptureBitmapAsync(null, false);

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
            var bitmap = await _captureService.CaptureBitmapAsync(_selectedRegion, _useSelectedRegion);
            if (bitmap == null) return;

            try
            {
                _lastImageData = await ImageProcessingService.ConvertBitmapToPngBytesAsync(bitmap);
                EnqueueBitmapForProcessing(bitmap);
            }
            catch (Exception)
            {
                bitmap.Dispose();
            }
        }

        private async Task<bool> CheckImageSimilarityAsync()
        {
            if (_lastImageData == null) return true;

            try
            {
                return await _captureService.HasImageChangedAsync(_lastImageData, _selectedRegion, _useSelectedRegion);
            }
            catch (Exception)
            {
                return true;
            }
        }
    }
}
