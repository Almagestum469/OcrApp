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
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }
        private async void CaptureOcrButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 捕获屏幕区域
                var picker = new GraphicsCapturePicker();
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                var item = await picker.PickSingleItemAsync();
                if (item == null)
                {
                    ResultListView.ItemsSource = new List<string> { "未选择捕获源" };
                    return;
                }

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
                            item.Size);
                var session = framePool.CreateCaptureSession(item);

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

                SoftwareBitmap? bitmap = null;
                try
                {
                    using var canvasBitmap = CanvasBitmap.CreateFromDirect3D11Surface(canvasDevice, capturedFrame.Surface);
                    var pixelBytes = canvasBitmap.GetPixelBytes();
                    bitmap = new SoftwareBitmap(
                        BitmapPixelFormat.Bgra8,
                        (int)canvasBitmap.SizeInPixels.Width,
                        (int)canvasBitmap.SizeInPixels.Height,
                        BitmapAlphaMode.Premultiplied);
                    bitmap.CopyFromBuffer(pixelBytes.AsBuffer());
                }
                catch (Exception ex)
                {
                    ResultListView.ItemsSource = new List<string> { $"转换位图失败: {ex.Message}" };
                    capturedFrame.Dispose();
                    session.Dispose();
                    framePool.Dispose();
                    return;
                }

                if (bitmap == null) // 理论上如果上面 try 块成功，这里不会是 null，但以防万一
                {
                    ResultListView.ItemsSource = new List<string> { "位图转换失败" };
                    capturedFrame.Dispose();
                    session.Dispose();
                    framePool.Dispose();
                    return;
                }

                // OCR
                var desiredLanguage = new Windows.Globalization.Language("en-US"); // 设置为英语（美国）
                var ocr = OcrEngine.TryCreateFromLanguage(desiredLanguage);

                if (ocr == null)
                {
                    // 如果指定语言不可用，尝试回退到用户默认设置
                    ResultListView.ItemsSource = new List<string> { $"无法使用指定语言 ({desiredLanguage.DisplayName}) 创建 OCR 引擎，尝试使用用户默认语言。" };
                    ocr = OcrEngine.TryCreateFromUserProfileLanguages();
                }

                if (ocr == null)
                {
                    ResultListView.ItemsSource = new List<string> { "无法创建 OCR 引擎" };
                    bitmap.Dispose();
                    capturedFrame.Dispose();
                    session.Dispose();
                    framePool.Dispose();
                    return;
                }

                var result = await ocr.RecognizeAsync(bitmap);
                if (result.Lines != null && result.Lines.Any())
                {
                    // var textLines = result.Lines.Select(line => line.Text).ToList();
                    // ResultListView.ItemsSource = textLines;
                    var paragraphs = OcrTextHelper.GroupLinesIntoParagraphs(result.Lines); // 使用新的帮助类
                    ResultListView.ItemsSource = paragraphs;
                }
                else
                {
                    ResultListView.ItemsSource = new List<string> { "未识别到文本" };
                }

                // 清理资源
                bitmap.Dispose();
                capturedFrame.Dispose();
                session.Dispose();
                framePool.Dispose();
            }
            catch (Exception ex)
            {
                ResultListView.ItemsSource = new List<string> { $"发生错误: {ex.Message}" };
            }
        }

        // Helper method to calculate the bounding box of an OcrLine
        // private static Windows.Foundation.Rect GetLineBoundingRect(OcrLine line) // 方法已移至 OcrTextHelper
        // {
        //     if (line.Words == null || !line.Words.Any())
        //     {
        //         return new Windows.Foundation.Rect(); // Return empty Rect if no words
        //     }

        //     double minX = line.Words.Min(word => word.BoundingRect.Left);
        //     double minY = line.Words.Min(word => word.BoundingRect.Top);
        //     double maxX = line.Words.Max(word => word.BoundingRect.Right);
        //     double maxY = line.Words.Max(word => word.BoundingRect.Bottom);

        //     return new Windows.Foundation.Rect(minX, minY, maxX - minX, maxY - minY);
        // }

        // private List<string> GroupLinesIntoParagraphs(IReadOnlyList<OcrLine> ocrLines) // 方法已移至 OcrTextHelper
        // {
        //     if (ocrLines == null || !ocrLines.Any())
        //     {
        //         return new List<string> { "未识别到文本" };
        //     }

        //     var paragraphs = new List<string>();
        //     if (!ocrLines.Any()) return paragraphs; // Should be caught by above, but good practice

        //     var currentParagraphBuilder = new System.Text.StringBuilder();
        //     currentParagraphBuilder.Append(ocrLines[0].Text);
        //     Rect previousLineRect = GetLineBoundingRect(ocrLines[0]);

        //     for (int i = 1; i < ocrLines.Count; i++)
        //     {
        //         var currentLine = ocrLines[i];
        //         Rect currentLineRect = GetLineBoundingRect(currentLine);

        //         // Calculate vertical distance between the bottom of the previous line and the top of the current line
        //         double verticalGap = currentLineRect.Top - previousLineRect.Bottom;

        //         // Heuristic for paragraph break: 
        //         // If the gap is larger than 75% of the previous line's height.
        //         // Also, ensure a minimum threshold (e.g., 5 pixels) to avoid breaks due to very small line heights.
        //         double paragraphBreakThreshold = previousLineRect.Height * 0.75;
        //         paragraphBreakThreshold = Math.Max(paragraphBreakThreshold, 5.0);

        //         if (verticalGap > paragraphBreakThreshold)
        //         {
        //             paragraphs.Add(currentParagraphBuilder.ToString());
        //             currentParagraphBuilder.Clear();
        //             currentParagraphBuilder.Append(currentLine.Text);
        //         }
        //         else
        //         {
        //             currentParagraphBuilder.Append(" ").Append(currentLine.Text);
        //         }
        //         previousLineRect = currentLineRect;
        //     }

        //     if (currentParagraphBuilder.Length > 0)
        //     {
        //         paragraphs.Add(currentParagraphBuilder.ToString());
        //     }

        //     if (!paragraphs.Any() && ocrLines.Any())
        //     {
        //         // Fallback if no paragraphs were formed but there was text
        //         var allText = string.Join(" ", ocrLines.Select(l => l.Text));
        //         if (!string.IsNullOrWhiteSpace(allText))
        //         {
        //             paragraphs.Add(allText);
        //         }
        //     }

        //     if (!paragraphs.Any()) // If still no paragraphs, means no text was effectively processed.
        //     {
        //         return new List<string> { "未能将文本组合成段落" };
        //     }

        //     return paragraphs;
        // }
    }
}
