using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CoenM.ImageHash;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OcrApp.Engines;
using OcrApp.Services;
using OcrApp.Tasks;
using OcrApp.Utils;
using Windows.Graphics.Capture;
using Windows.Graphics.Imaging;
using WinRT.Interop;

namespace OcrApp
{
  public sealed partial class TranslationOverlay
  {
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x8;
    private const int WS_EX_LAYERED = 0x80000;
    private const uint LWA_ALPHA = 0x2;
    private const uint SWP_NOMOVE = 0x2;
    private const uint SWP_NOSIZE = 0x1;
    private const uint SWP_SHOWWINDOW = 0x40;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);

    private bool _isPinned = true;
    private bool _isDragging;
    private bool _isTranslationVisible = true;
    private Windows.Graphics.PointInt32 _lastPointerPosition;

    private GraphicsCaptureItem? _captureItem;
    private IOcrEngine? _ocrEngine;
    private Windows.Graphics.RectInt32? _selectedRegion;
    private bool _useSelectedRegion;
    private OcrTaskPipeline? _taskPipeline;
    private bool _isAutoModeEnabled;
    private CancellationTokenSource? _autoModeCancellationTokenSource;
    private ulong? _lastImageHash;
    private readonly ScreenCaptureService _captureService = new();

    private Brush? _defaultTranslationBrush;
    private readonly SolidColorBrush _errorTranslationBrush = new(Microsoft.UI.Colors.OrangeRed);
    private readonly SolidColorBrush _autoModeActiveBrush = new(Microsoft.UI.Colors.MediumSeaGreen);
    private readonly SolidColorBrush _autoModeInactiveBrush = new(Windows.UI.Color.FromArgb(64, 255, 255, 255));

    public TranslationOverlay()
    {
      InitializeComponent();

      _defaultTranslationBrush = TranslationTextBlock.Foreground;

      Activated += TranslationOverlay_Activated;
      Closed += TranslationOverlay_Closed;

      ExtendsContentIntoTitleBar = true;
      SetTitleBar(null);

      InitializeWindowChrome();
      InitializeOcrInfrastructure();
      UpdateAutoModeButtonVisual();
    }

    private void InitializeWindowChrome()
    {
      AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 220));

      var displayArea = Microsoft.UI.Windowing.DisplayArea.Primary;
      var workArea = displayArea.WorkArea;
      var x = (workArea.Width - 600) / 2;
      var y = 50;
      AppWindow.Move(new Windows.Graphics.PointInt32(x, y));

      SetWindowStyle();
      SetTopMost();

      if (Content is Grid grid)
      {
        grid.PointerPressed += Grid_PointerPressed;
        grid.PointerMoved += Grid_PointerMoved;
        grid.PointerReleased += Grid_PointerReleased;
      }
    }

    private void InitializeOcrInfrastructure()
    {
      _ocrEngine = new PaddleOcrEngine();
      InitializePaddleEngineAsync();
      InitializeTaskPipeline();

      _captureService.CaptureFailed += (s, e) =>
      {
        DispatcherQueue.TryEnqueue(() =>
        {
          ShowError("捕获会话创建失败");
          SetOcrStatus("捕获失败", Microsoft.UI.Colors.Red);
        });
      };
    }

    private void TranslationOverlay_Activated(object sender, WindowActivatedEventArgs args)
    {
      SetWindowStyle();
      SetTopMost();
    }

    private void TranslationOverlay_Closed(object sender, WindowEventArgs args)
    {
      StopAutoMode();

      _taskPipeline?.Dispose();
      _captureService.Dispose();
    }

    private void InitializePaddleEngineAsync()
    {
      _ = InitializePaddleEngineInternalAsync();
    }

    private async Task InitializePaddleEngineInternalAsync()
    {
      SetOcrStatus("正在初始化PaddleOCR...", Microsoft.UI.Colors.Orange);

      var success = await _ocrEngine!.InitializeAsync();
      if (success)
      {
        SetOcrStatus("PaddleOCR就绪", Microsoft.UI.Colors.Green);
      }
      else
      {
        SetOcrStatus("PaddleOCR初始化失败", Microsoft.UI.Colors.Red);
        ShowError("PaddleOCR初始化失败");
      }
    }

    private void InitializeTaskPipeline()
    {
      _taskPipeline?.Dispose();
      _taskPipeline = new OcrTaskPipeline(() => _ocrEngine);

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
          DisplayRecognizedText(task.OcrTexts);
          SetOcrStatus("OCR完成", Microsoft.UI.Colors.Green);
        }

        if (task.IsTranslated && task.Translations != null)
        {
          UpdateWithTranslations(task.Translations);
        }
      });
    }

    private void TaskPipeline_PipelineError(Exception ex)
    {
      DispatcherQueue.TryEnqueue(() =>
      {
        ShowError($"管线错误: {ex.Message}");
        SetOcrStatus("任务错误", Microsoft.UI.Colors.Red);
      });
    }

    private void EnqueueBitmapForProcessing(SoftwareBitmap bitmap)
    {
      if (_taskPipeline == null)
      {
        ShowError("任务管线未就绪");
        SetOcrStatus("任务管线未就绪", Microsoft.UI.Colors.Red);
        bitmap.Dispose();
        return;
      }

      var task = new OcrTask(bitmap);
      _taskPipeline.Enqueue(task);
      SetOcrStatus("队列中...", Microsoft.UI.Colors.Orange);
    }

    private async void SelectCaptureItemButton_Click(object sender, RoutedEventArgs e)
    {
      var picker = new GraphicsCapturePicker();
      var hwnd = WindowNative.GetWindowHandle(this);
      InitializeWithWindow.Initialize(picker, hwnd);
      _captureItem = await picker.PickSingleItemAsync();

      if (_captureItem != null)
      {
        ShowInfo($"已选择: {_captureItem.DisplayName}");
        SetOcrStatus("捕获源已选择", Microsoft.UI.Colors.Green);
        RecognizeButton.IsEnabled = true;
        SelectRegionButton.IsEnabled = true;

        var sessionCreated = await _captureService.InitializeAsync(_captureItem);
        if (!sessionCreated)
        {
          ShowError("捕获会话创建失败");
          SetOcrStatus("捕获失败", Microsoft.UI.Colors.Red);
        }
        else
        {
          SelectRegionButton_Click(SelectRegionButton, new RoutedEventArgs());
        }
      }
      else
      {
        ShowInfo("未选择捕获源");
        SetOcrStatus("等待捕获源", Microsoft.UI.Colors.Orange);
        RecognizeButton.IsEnabled = false;
        SelectRegionButton.IsEnabled = false;
        AutoModeButton.IsEnabled = false;
        if (_isAutoModeEnabled)
        {
          StopAutoMode();
        }
        UpdateAutoModeButtonVisual();

        _captureService.Dispose();
      }
    }

    private async void RecognizeButton_Click(object sender, RoutedEventArgs e)
    {
      if (_captureItem == null)
      {
        ShowError("请先选择捕获窗口");
        SetOcrStatus("等待捕获源", Microsoft.UI.Colors.Orange);
        return;
      }

      if (_ocrEngine == null)
      {
        ShowError("OCR引擎未初始化");
        SetOcrStatus("OCR未就绪", Microsoft.UI.Colors.Red);
        return;
      }

      try
      {
        var processed = await CaptureAndProcessAsync(forceProcess: true);
        if (!processed)
        {
          ShowError("捕获失败：无法获取帧");
          SetOcrStatus("捕获失败", Microsoft.UI.Colors.Red);
        }
      }
      catch (Exception ex)
      {
        ShowError($"发生错误: {ex.Message}");
        SetOcrStatus("OCR失败", Microsoft.UI.Colors.Red);
      }
    }

    private async void SelectRegionButton_Click(object sender, RoutedEventArgs e)
    {
      if (_captureItem == null)
      {
        ShowError("请先选择捕获窗口");
        SetOcrStatus("等待捕获源", Microsoft.UI.Colors.Orange);
        return;
      }

      try
      {
        var previewBitmap = await _captureService.CaptureBitmapAsync(null, false);
        if (previewBitmap == null)
        {
          ShowError("位图转换失败");
          SetOcrStatus("预览失败", Microsoft.UI.Colors.Red);
          return;
        }

        var regionSelector = new RegionSelector();
        regionSelector.SetCapturedBitmap(previewBitmap);
        regionSelector.SelectionConfirmed += (s, region) =>
        {
          if (region != null)
          {
            _selectedRegion = region;
            _useSelectedRegion = true;

            DispatcherQueue.TryEnqueue(async () =>
            {
              ShowInfo($"已设置识别区域: X={region.Value.X}, Y={region.Value.Y}, 宽={region.Value.Width}, 高={region.Value.Height}");
              AutoModeButton.IsEnabled = true;
              UpdateAutoModeButtonVisual();

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
              ShowInfo("未设置识别区域，将识别整个窗口");
              AutoModeButton.IsEnabled = false;
              if (_isAutoModeEnabled)
              {
                StopAutoMode();
              }
              UpdateAutoModeButtonVisual();
            });
          }
        };

        regionSelector.Activate();
      }
      catch (Exception ex)
      {
        ShowError($"设置识别区域时出错: {ex.Message}");
        SetOcrStatus("区域选择失败", Microsoft.UI.Colors.Red);
      }
    }

    private void AutoModeButton_Click(object sender, RoutedEventArgs e)
    {
      if (!_isAutoModeEnabled)
      {
        StartAutoMode();
      }
      else
      {
        StopAutoMode();
      }
    }

    private void UpdateAutoModeButtonVisual()
    {
      if (AutoModeButton == null)
      {
        return;
      }

      AutoModeButton.Content = _isAutoModeEnabled ? "AUTO ON" : "AUTO OFF";
      AutoModeButton.Background = _isAutoModeEnabled ? _autoModeActiveBrush : _autoModeInactiveBrush;
      AutoModeButton.Opacity = AutoModeButton.IsEnabled ? 1 : 0.5;
    }

    private void StartAutoMode()
    {
      if (_isAutoModeEnabled)
      {
        UpdateAutoModeButtonVisual();
        return;
      }

      _isAutoModeEnabled = true;
      _autoModeCancellationTokenSource = new CancellationTokenSource();
      _lastImageHash = null;

      _ = AutoModeLoopAsync(_autoModeCancellationTokenSource.Token);
      UpdateAutoModeButtonVisual();
    }

    private void StopAutoMode()
    {
      if (!_isAutoModeEnabled)
      {
        UpdateAutoModeButtonVisual();
        return;
      }

      _isAutoModeEnabled = false;
      _autoModeCancellationTokenSource?.Cancel();
      _autoModeCancellationTokenSource?.Dispose();
      _autoModeCancellationTokenSource = null;
      _lastImageHash = null;
      UpdateAutoModeButtonVisual();
    }

    private async Task AutoModeLoopAsync(CancellationToken cancellationToken)
    {
      try
      {
        await CaptureAndProcessAsync(forceProcess: true, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
          await Task.Delay(500, cancellationToken);
          await CaptureAndProcessAsync(forceProcess: false, cancellationToken);
        }
      }
      catch (OperationCanceledException)
      {
      }
      catch (Exception)
      {
        DispatcherQueue.TryEnqueue(() =>
        {
          StopAutoMode();
          ShowError("自动模式发生错误，已停止");
          SetOcrStatus("自动模式错误", Microsoft.UI.Colors.Red);
        });
      }
    }

    private async Task<bool> CaptureAndProcessAsync(bool forceProcess, CancellationToken cancellationToken = default)
    {
      if (_captureItem == null || _ocrEngine == null)
      {
        return false;
      }

      var bitmap = await _captureService.CaptureBitmapAsync(_selectedRegion, _useSelectedRegion, cancellationToken);
      if (bitmap == null)
      {
        return false;
      }

      try
      {
        var currentHash = await _captureService.ComputeHashAsync(bitmap, cancellationToken);

        if (!forceProcess && _lastImageHash.HasValue && currentHash.HasValue)
        {
          var similarity = CompareHash.Similarity(_lastImageHash.Value, currentHash.Value);
          if (similarity >= 99.0)
          {
            bitmap.Dispose();
            return false;
          }
        }

        if (currentHash.HasValue)
        {
          _lastImageHash = currentHash;
        }

        EnqueueBitmapForProcessing(bitmap);
        return true;
      }
      catch (OperationCanceledException)
      {
        bitmap.Dispose();
        throw;
      }
      catch (Exception)
      {
        bitmap.Dispose();
        throw;
      }
    }

    private void SetOcrStatus(string status, Windows.UI.Color color)
    {
      RecognitionStatusTextBlock.Text = status;
      RecognitionStatusTextBlock.Foreground = new SolidColorBrush(color);
    }

    private void ShowInfo(string message)
    {
      TranslationTextBlock.Foreground = _defaultTranslationBrush ?? new SolidColorBrush(Microsoft.UI.Colors.Gold);
      TranslationTextBlock.Text = message;
    }

    private void ShowError(string message)
    {
      TranslationTextBlock.Foreground = _errorTranslationBrush;
      TranslationTextBlock.Text = message;
    }

    private void DisplayRecognizedText(IEnumerable<string> texts)
    {
      if (texts == null)
      {
        return;
      }

      TranslationTextBlock.Foreground = _defaultTranslationBrush ?? new SolidColorBrush(Microsoft.UI.Colors.Gold);
      TranslationTextBlock.Text = string.Join("\n", texts);
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
      _isPinned = !_isPinned;
      SetTopMost();
    }

    private void SetTopMost()
    {
      var hwnd = WindowNative.GetWindowHandle(this);
      var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);

      if (_isPinned)
      {
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

        if ((exStyle & WS_EX_TOPMOST) == 0)
        {
          exStyle |= WS_EX_TOPMOST;
          SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
        }

        PinButton.Content = "📌";
      }
      else
      {
        SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

        if ((exStyle & WS_EX_TOPMOST) != 0)
        {
          exStyle &= ~WS_EX_TOPMOST;
          SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
        }

        PinButton.Content = "📍";
      }
    }

    private void SetWindowStyle()
    {
      var hwnd = WindowNative.GetWindowHandle(this);
      var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
      exStyle |= WS_EX_LAYERED;

      if (_isPinned)
      {
        exStyle |= WS_EX_TOPMOST;
      }
      else
      {
        exStyle &= ~WS_EX_TOPMOST;
      }

      SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
      SetLayeredWindowAttributes(hwnd, 0, 220, LWA_ALPHA);
    }

    public void UpdateTranslation(string originalText, string translatedText)
    {
      TranslationTextBlock.Text = string.IsNullOrEmpty(translatedText) ? "无翻译结果" : translatedText;
    }

    public void UpdateRecognitionStatus(string status)
    {
      SetOcrStatus(status, Microsoft.UI.Colors.White);
    }

    public async void UpdateWithOcrResults(List<string> ocrResults)
    {
      if (ocrResults == null || ocrResults.Count == 0)
      {
        SetOcrStatus("无识别结果", Microsoft.UI.Colors.Orange);
        ShowInfo(string.Empty);
        return;
      }

      try
      {
        SetOcrStatus("翻译中...", Microsoft.UI.Colors.Orange);
        TranslationTextBlock.Text = string.Empty;

        var translatedResults = new List<string>();
        foreach (var text in ocrResults.Where(text => !string.IsNullOrWhiteSpace(text)))
        {
          var translation = await GoogleTranslator.TranslateEnglishToChineseAsync(text);
          translatedResults.Add(translation);
        }

        if (translatedResults.Count == 0)
        {
          SetOcrStatus("无可翻译内容", Microsoft.UI.Colors.Orange);
          ShowInfo(string.Empty);
          return;
        }

        TranslationTextBlock.Foreground = _defaultTranslationBrush ?? new SolidColorBrush(Microsoft.UI.Colors.Gold);
        TranslationTextBlock.Text = string.Join("\n", translatedResults);
        SetOcrStatus("翻译完成", Microsoft.UI.Colors.Green);
      }
      catch (Exception ex)
      {
        SetOcrStatus("翻译失败", Microsoft.UI.Colors.Red);
        ShowError($"翻译失败: {ex.Message}");
      }
    }

    public void UpdateWithTranslations(IReadOnlyList<string> translations)
    {
      if (translations == null || translations.Count == 0)
      {
        SetOcrStatus("无翻译结果", Microsoft.UI.Colors.Orange);
        ShowInfo(string.Empty);
        return;
      }

      TranslationTextBlock.Foreground = _defaultTranslationBrush ?? new SolidColorBrush(Microsoft.UI.Colors.Gold);
      TranslationTextBlock.Text = string.Join("\n", translations);
      SetOcrStatus("翻译完成", Microsoft.UI.Colors.Green);
    }

    private void Grid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
      if (sender is Grid grid)
      {
        _isDragging = true;
        _lastPointerPosition = new Windows.Graphics.PointInt32(
          (int)e.GetCurrentPoint(grid).Position.X,
          (int)e.GetCurrentPoint(grid).Position.Y);
        grid.CapturePointer(e.Pointer);
        e.Handled = true;
      }
    }

    private void Grid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
      if (!_isDragging || sender is not Grid grid)
      {
        return;
      }

      var currentPosition = e.GetCurrentPoint(grid).Position;
      var deltaX = (int)currentPosition.X - _lastPointerPosition.X;
      var deltaY = (int)currentPosition.Y - _lastPointerPosition.Y;

      var currentPos = AppWindow.Position;
      AppWindow.Move(new Windows.Graphics.PointInt32(currentPos.X + deltaX, currentPos.Y + deltaY));

      e.Handled = true;
    }

    private void Grid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
      if (!_isDragging)
      {
        return;
      }

      _isDragging = false;
      if (sender is Grid grid)
      {
        grid.ReleasePointerCapture(e.Pointer);
      }

      e.Handled = true;
    }
  }
}
