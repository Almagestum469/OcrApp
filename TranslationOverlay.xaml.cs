using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OcrApp.Utils;
using WinRT.Interop;

namespace OcrApp
{
  public sealed partial class TranslationOverlay : Window
  {
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x8;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const uint SWP_NOMOVE = 0x2;
    private const uint SWP_NOSIZE = 0x1;
    private const uint SWP_SHOWWINDOW = 0x40;
    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

    private bool _isPinned = true;
    private bool _isDragging = false;
    private Windows.Graphics.PointInt32 _lastPointerPosition;

    public TranslationOverlay()
    {
      this.InitializeComponent();
      this.Activated += TranslationOverlay_Activated;
      this.Closed += TranslationOverlay_Closed;

      // 设置初始位置和大小
      this.AppWindow.Resize(new Windows.Graphics.SizeInt32(600, 200));

      // 移动到屏幕顶部中央
      var displayArea = Microsoft.UI.Windowing.DisplayArea.Primary;
      var workArea = displayArea.WorkArea;
      var x = (workArea.Width - 600) / 2;
      var y = 50; // 距离顶部50像素
      this.AppWindow.Move(new Windows.Graphics.PointInt32(x, y));

      // 启用拖拽功能
      var grid = this.Content as Grid;
      if (grid != null)
      {
        grid.PointerPressed += Grid_PointerPressed;
        grid.PointerMoved += Grid_PointerMoved;
        grid.PointerReleased += Grid_PointerReleased;
      }
    }

    private void TranslationOverlay_Activated(object sender, WindowActivatedEventArgs args)
    {
      // 确保窗口置顶
      SetTopMost();
    }

    private void TranslationOverlay_Closed(object sender, WindowEventArgs args)
    {
      // 清理资源
    }

    private void SetTopMost()
    {
      var hwnd = WindowNative.GetWindowHandle(this);
      if (_isPinned)
      {
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

        // 设置为工具窗口，避免在任务栏显示
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW;
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
      }
      else
      {
        SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
      }
    }

    public void UpdateTranslation(string originalText, string translatedText)
    {
      OriginalTextBlock.Text = string.IsNullOrEmpty(originalText) ? "无原文" : originalText;
      TranslationTextBlock.Text = string.IsNullOrEmpty(translatedText) ? "无翻译结果" : translatedText;
    }

    public async void UpdateWithOcrResults(System.Collections.Generic.List<string> ocrResults)
    {
      if (ocrResults == null || ocrResults.Count == 0)
      {
        UpdateTranslation("", "无识别结果");
        return;
      }

      // 合并OCR结果
      var combinedText = string.Join(" ", ocrResults);
      OriginalTextBlock.Text = combinedText;

      // 异步翻译
      try
      {
        TranslationTextBlock.Text = "翻译中...";
        var translation = await GoogleTranslator.TranslateEnglishToChineseAsync(combinedText);
        TranslationTextBlock.Text = translation;
      }
      catch (Exception ex)
      {
        TranslationTextBlock.Text = $"翻译失败: {ex.Message}";
      }
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
      _isPinned = !_isPinned;
      PinButton.Content = _isPinned ? "📌" : "📍";
      SetTopMost();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
      this.Close();
    }

    // 拖拽功能实现
    private void Grid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
      var grid = sender as Grid;
      if (grid != null)
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
      if (_isDragging)
      {
        var grid = sender as Grid;
        if (grid != null)
        {
          var currentPosition = e.GetCurrentPoint(grid).Position;
          var deltaX = (int)currentPosition.X - _lastPointerPosition.X;
          var deltaY = (int)currentPosition.Y - _lastPointerPosition.Y;

          var currentPos = this.AppWindow.Position;
          this.AppWindow.Move(new Windows.Graphics.PointInt32(
              currentPos.X + deltaX,
              currentPos.Y + deltaY));

          e.Handled = true;
        }
      }
    }

    private void Grid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
      if (_isDragging)
      {
        _isDragging = false;
        var grid = sender as Grid;
        grid?.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
      }
    }
  }
}
