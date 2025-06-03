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
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong); [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x8;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_LAYERED = 0x80000;
    private const uint LWA_ALPHA = 0x2;
    private const uint SWP_NOMOVE = 0x2;
    private const uint SWP_NOSIZE = 0x1;
    private const uint SWP_SHOWWINDOW = 0x40; private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1); private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    private bool _isPinned = false;
    private bool _isDragging = false;
    private Windows.Graphics.PointInt32 _lastPointerPosition; public TranslationOverlay()
    {
      this.InitializeComponent();
      this.Activated += TranslationOverlay_Activated;
      this.Closed += TranslationOverlay_Closed;

      // 设置窗口样式 - 移除标题栏和边框
      this.ExtendsContentIntoTitleBar = true;
      this.SetTitleBar(null);      // 设置初始位置和大小 - 窗口调整为更大
      this.AppWindow.Resize(new Windows.Graphics.SizeInt32(600, 200));      // 移动到屏幕顶部中央
      var displayArea = Microsoft.UI.Windowing.DisplayArea.Primary;
      var workArea = displayArea.WorkArea;
      var x = (workArea.Width - 600) / 2;
      var y = 50; // 距离顶部50像素
      this.AppWindow.Move(new Windows.Graphics.PointInt32(x, y));      // 立即设置窗口样式和置顶状态
      SetWindowStyle();
      SetTopMost();

      // 确保按钮状态与默认不置顶状态一致
      PinButton.Content = "📍";

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
      // 设置窗口样式并确保窗口置顶
      SetWindowStyle();

      // 确保窗口置顶状态正确
      SetTopMost();
    }

    private void TranslationOverlay_Closed(object sender, WindowEventArgs args)
    {
      // 清理资源
    }
    private void SetTopMost()
    {
      var hwnd = WindowNative.GetWindowHandle(this);

      // 先获取当前样式
      var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);

      if (_isPinned)
      {
        // 使用 SetWindowPos 设置窗口为置顶
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

        // 确保 WS_EX_TOPMOST 样式被设置
        if ((exStyle & WS_EX_TOPMOST) == 0)
        {
          exStyle |= WS_EX_TOPMOST;
          SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
        }

        // 更新 UI 以反映当前状态
        PinButton.Content = "📌";
      }
      else
      {
        // 使用 SetWindowPos 取消窗口置顶
        SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

        // 确保 WS_EX_TOPMOST 样式被移除
        if ((exStyle & WS_EX_TOPMOST) != 0)
        {
          exStyle &= ~WS_EX_TOPMOST;
          SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
        }

        // 更新 UI 以反映当前状态
        PinButton.Content = "📍";
      }
    }
    private void SetWindowStyle()
    {
      var hwnd = WindowNative.GetWindowHandle(this);

      // 设置为工具窗口样式，避免在任务栏显示，并启用分层窗口
      var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
      // exStyle |= WS_EX_TOOLWINDOW | WS_EX_LAYERED; // 移除 WS_EX_TOOLWINDOW
      exStyle |= WS_EX_LAYERED; // 只保留 WS_EX_LAYERED

      // 如果当前应该置顶，确保加上置顶标志
      if (_isPinned)
      {
        exStyle |= WS_EX_TOPMOST;
      }
      else
      {
        exStyle &= ~WS_EX_TOPMOST;
      }

      SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

      // 设置窗口透明度 (0-255, 255为完全不透明)
      SetLayeredWindowAttributes(hwnd, 0, 220, LWA_ALPHA);
    }

    public void UpdateTranslation(string originalText, string translatedText)
    {
      TranslationTextBlock.Text = string.IsNullOrEmpty(translatedText) ? "无翻译结果" : translatedText;
    }

    public void UpdateRecognitionStatus(string status)
    {
      TranslationTextBlock.Text = status;
    }
    public async void UpdateWithOcrResults(System.Collections.Generic.List<string> ocrResults)
    {
      if (ocrResults == null || ocrResults.Count == 0)
      {
        TranslationTextBlock.Text = "无识别结果";
        return;
      }

      try
      {
        TranslationTextBlock.Text = "翻译中...";

        // 存储所有翻译结果
        System.Collections.Generic.List<string> translatedResults = new System.Collections.Generic.List<string>();

        // 为每个OCR结果进行翻译
        foreach (var text in ocrResults)
        {
          // 跳过空白内容
          if (string.IsNullOrWhiteSpace(text))
          {
            continue;
          }

          // 计算单词数
          int wordCount = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

          // 如果单词数小于3，直接添加原文
          if (wordCount < 3)
          {
            translatedResults.Add(text);
          }
          else
          {
            // 否则翻译内容
            var translation = await GoogleTranslator.TranslateEnglishToChineseAsync(text);
            translatedResults.Add(translation);
          }
        }

        // 检查是否有任何翻译结果
        if (translatedResults.Count == 0)
        {
          TranslationTextBlock.Text = "无可翻译内容";
          return;
        }

        // 将所有翻译结果合并为一个字符串，每个结果占一行
        TranslationTextBlock.Text = string.Join("\n", translatedResults);
      }
      catch (Exception ex)
      {
        TranslationTextBlock.Text = $"翻译失败: {ex.Message}";
      }
    }
    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
      // 切换置顶状态
      _isPinned = !_isPinned;

      // 调用 SetTopMost 应用置顶状态变更
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
