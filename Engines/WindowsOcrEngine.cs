using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;


namespace OcrApp.Engines
{
  public class WindowsOcrEngine : IOcrEngine
  {
    private OcrEngine? _ocrEngine;
    private OcrResult? _lastOcrResult;

    public Task<bool> InitializeAsync()
    {
      var desiredLanguage = new Windows.Globalization.Language("en-US");
      _ocrEngine = OcrEngine.TryCreateFromLanguage(desiredLanguage) ?? OcrEngine.TryCreateFromUserProfileLanguages();
      return Task.FromResult(_ocrEngine != null);
    }
    public async Task<List<string>> RecognizeAsync(SoftwareBitmap bitmap)
    {
      if (_ocrEngine == null) return new List<string> { "Windows OCR引擎未初始化" };
      _lastOcrResult = await _ocrEngine.RecognizeAsync(bitmap);
      if (_lastOcrResult.Lines != null && _lastOcrResult.Lines.Any())
      {
        // 按位置排序所有行，然后直接合并为一段文本
        var sortedLines = _lastOcrResult.Lines
          .OrderBy(line =>
          {
            if (line.Words != null && line.Words.Any())
            {
              return line.Words.Min(w => w.BoundingRect.Top) + line.Words.Max(w => w.BoundingRect.Bottom);
            }
            return 0;
          })
          .ThenBy(line =>
          {
            if (line.Words != null && line.Words.Any())
            {
              return line.Words.Min(w => w.BoundingRect.Left);
            }
            return 0;
          })
          .ToList();

        var allText = string.Join(" ", sortedLines.Select(line => line.Text));
        if (!string.IsNullOrWhiteSpace(allText))
        {
          return new List<string> { allText };
        }
        else
        {
          return new List<string> { "识别到的文本为空" };
        }
      }
      return new List<string> { "未识别到文本" };
    }

    public string GenerateDebugInfo()
    {
      if (_lastOcrResult == null) return "无识别结果数据";
      var debugInfo = new StringBuilder();
      debugInfo.AppendLine("=== Windows OCR 识别结果详细信息 ===");
      debugInfo.AppendLine($"识别时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
      debugInfo.AppendLine($"文本角度: {_lastOcrResult.TextAngle?.ToString() ?? "N/A"}°");
      debugInfo.AppendLine($"总行数: {_lastOcrResult.Lines?.Count ?? 0}");
      debugInfo.AppendLine();
      if (_lastOcrResult.Lines != null && _lastOcrResult.Lines.Any())
      {
        for (int lineIndex = 0; lineIndex < _lastOcrResult.Lines.Count; lineIndex++)
        {
          var line = _lastOcrResult.Lines[lineIndex];
          debugInfo.AppendLine($"--- 第 {lineIndex + 1} 行 ---");
          debugInfo.AppendLine($"行文本: \"{line.Text}\"");
          if (line.Words != null && line.Words.Any())
          {
            var minX = line.Words.Min(w => w.BoundingRect.X);
            var minY = line.Words.Min(w => w.BoundingRect.Y);
            var maxX = line.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
            var maxY = line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
            debugInfo.AppendLine($"行边界: X={minX:F1}, Y={minY:F1}, W={maxX - minX:F1}, H={maxY - minY:F1}");
          }
          debugInfo.AppendLine($"单词数量: {line.Words?.Count ?? 0}");
          if (line.Words != null && line.Words.Any())
          {
            for (int wordIndex = 0; wordIndex < line.Words.Count; wordIndex++)
            {
              var word = line.Words[wordIndex];
              debugInfo.AppendLine($"  单词 {wordIndex + 1}: \"{word.Text}\"");
              debugInfo.AppendLine($"    边界: X={word.BoundingRect.X:F1}, Y={word.BoundingRect.Y:F1}, W={word.BoundingRect.Width:F1}, H={word.BoundingRect.Height:F1}");
            }
          }
          debugInfo.AppendLine();
        }
      }
      else
      {
        debugInfo.AppendLine("未检测到任何文本行");
      }
      debugInfo.AppendLine("=== 调试信息结束 ==="); return debugInfo.ToString();
    }
  }
}
