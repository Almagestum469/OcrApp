using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using OcrApp.Utils;

namespace OcrApp.Engines
{
  public class WindowsOcrEngine : IOcrEngine
  {
    private OcrEngine? _ocrEngine;
    private OcrResult? _lastOcrResult;

    // 段落分组算法参数
    private double _verticalBreakThresholdMultiplier = 1.5;
    private double _horizontalOverlapThreshold = 0.3;
    private double _heightDifferenceThresholdMultiplier = 1.5;

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
        return await GroupLinesIntoParagraphs(_lastOcrResult.Lines);
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
      debugInfo.AppendLine("=== 调试信息结束 ===");
      return debugInfo.ToString();
    }

    public void SetParagraphGroupingParameters(
      double verticalMultiplier = 1.5,
      double horizontalOverlapThreshold = 0.3,
      double heightDifferenceMultiplier = 1.5)
    {
      _verticalBreakThresholdMultiplier = verticalMultiplier;
      _horizontalOverlapThreshold = horizontalOverlapThreshold;
      _heightDifferenceThresholdMultiplier = heightDifferenceMultiplier;
    }

    private class LineBounds
    {
      public OcrLine Line { get; }
      public double Left { get; }
      public double Right { get; }
      public double Top { get; }
      public double Bottom { get; }
      public double Width { get; }
      public double Height { get; }
      public LineBounds(OcrLine line)
      {
        Line = line;
        if (line.Words != null && line.Words.Any())
        {
          Left = line.Words.Min(w => w.BoundingRect.Left);
          Right = line.Words.Max(w => w.BoundingRect.Right);
          Top = line.Words.Min(w => w.BoundingRect.Top);
          Bottom = line.Words.Max(w => w.BoundingRect.Bottom);
          Width = Right - Left;
          Height = Bottom - Top;
        }
        else
        {
          Left = Right = Top = Bottom = Width = Height = 0;
        }
      }
    }

    private async Task<List<string>> GroupLinesIntoParagraphs(IReadOnlyList<OcrLine> ocrLines)
    {
      if (ocrLines == null || !ocrLines.Any())
        return new List<string> { "未识别到文本" };

      var lineBoundsList = ocrLines.Select(l => new LineBounds(l)).ToList();
      var paragraphGroups = new List<List<LineBounds>>();

      foreach (var lineBounds in lineBoundsList)
      {
        bool added = false;
        foreach (var group in paragraphGroups)
        {
          if (CanMergeWithGroup(lineBounds, group))
          {
            group.Add(lineBounds);
            added = true;
            break;
          }
        }
        if (!added)
        {
          paragraphGroups.Add(new List<LineBounds> { lineBounds });
        }
      }

      var paragraphs = new List<string>();
      foreach (var group in paragraphGroups)
      {
        var sortedGroup = group.OrderBy(l => l.Top).ThenBy(l => l.Left).ToList();
        var paragraphText = string.Join(" ", sortedGroup.Select(l => l.Line.Text));
        if (!string.IsNullOrWhiteSpace(paragraphText))
        {
          paragraphs.Add(paragraphText); // 直接添加原文，不再翻译
        }
      }
      return paragraphs.Any() ? paragraphs : new List<string> { "未能将文本组合成段落" };
    }

    private bool CanMergeWithGroup(LineBounds line, List<LineBounds> group)
    {
      foreach (var groupLine in group)
      {
        double avgHeight = (line.Height + groupLine.Height) / 2.0;
        double verticalThreshold = avgHeight * _verticalBreakThresholdMultiplier;
        double verticalGap = Math.Abs(((line.Top + line.Bottom) / 2.0) - ((groupLine.Top + groupLine.Bottom) / 2.0));
        if (verticalGap > verticalThreshold)
        {
          continue;
        }
        double height1 = line.Height;
        double height2 = groupLine.Height;
        if (height1 > 0 && height2 > 0)
        {
          double larger = Math.Max(height1, height2);
          double smaller = Math.Min(height1, height2);
          if (larger > smaller * _heightDifferenceThresholdMultiplier)
          {
            continue;
          }
        }
        double overlap = CalculateHorizontalOverlapByBounds(line, groupLine);
        if (overlap >= _horizontalOverlapThreshold)
        {
          return true;
        }
      }
      return false;
    }

    private double CalculateHorizontalOverlapByBounds(LineBounds a, LineBounds b)
    {
      double overlapStart = Math.Max(a.Left, b.Left);
      double overlapEnd = Math.Min(a.Right, b.Right);
      double overlapWidth = Math.Max(0, overlapEnd - overlapStart);
      if (overlapWidth <= 0) return 0.0;
      double smallerWidth = Math.Min(a.Width, b.Width);
      return smallerWidth > 0 ? overlapWidth / smallerWidth : 0.0;
    }

    private class ProcessedLine
    {
      public string Text { get; }
      public IReadOnlyList<OcrWord> Words { get; }
      public Windows.Foundation.Rect BoundingRect { get; }
      public double AverageWordHeight { get; }
      public ProcessedLine(IReadOnlyList<OcrWord> words)
      {
        Words = words ?? new List<OcrWord>();
        Text = string.Join(" ", Words.Select(w => w.Text));
        BoundingRect = CalculateBoundingRectForWords(Words);
        AverageWordHeight = CalculateAverageWordHeightForWords(Words);
      }
      private static Windows.Foundation.Rect CalculateBoundingRectForWords(IReadOnlyList<OcrWord> words)
      {
        if (words == null || !words.Any()) return new Windows.Foundation.Rect(0, 0, 0, 0);
        double minX = words.Min(word => word.BoundingRect.Left);
        double minY = words.Min(word => word.BoundingRect.Top);
        double maxX = words.Max(word => word.BoundingRect.Right);
        double maxY = words.Max(word => word.BoundingRect.Bottom);
        return new Windows.Foundation.Rect(minX, minY, Math.Max(0, maxX - minX), Math.Max(0, maxY - minY));
      }
      private static double CalculateAverageWordHeightForWords(IReadOnlyList<OcrWord> words)
      {
        if (words == null || !words.Any()) return 0;
        var validWords = words.Where(w => w.BoundingRect.Height > 0).ToList();
        if (!validWords.Any()) return 0;
        return validWords.Average(word => word.BoundingRect.Height);
      }
    }
    private List<ProcessedLine> ExpandOcrLines(IReadOnlyList<OcrLine> ocrLines, Func<OcrLine, double> getAvgWordHeightFunc)
    {
      var expandedLines = new List<ProcessedLine>();
      if (ocrLines == null || !ocrLines.Any())
      {
        return expandedLines;
      }
      const double GAP_THRESHOLD_FACTOR = 1.0;
      const double MIN_AVG_WORD_HEIGHT_FOR_SPLIT = 5.0;
      foreach (var ocrLine in ocrLines)
      {
        if (ocrLine.Words == null || !ocrLine.Words.Any())
        {
          continue;
        }
        if (ocrLine.Words.Count <= 1)
        {
          expandedLines.Add(new ProcessedLine(ocrLine.Words.ToList()));
          continue;
        }
        double originalLineAvgWordHeight = getAvgWordHeightFunc(ocrLine);
        double splitThreshold = 0;
        if (originalLineAvgWordHeight >= MIN_AVG_WORD_HEIGHT_FOR_SPLIT)
        {
          splitThreshold = GAP_THRESHOLD_FACTOR * originalLineAvgWordHeight;
        }
        var currentWordGroup = new List<OcrWord>();
        currentWordGroup.Add(ocrLine.Words[0]);
        for (int i = 0; i < ocrLine.Words.Count - 1; i++)
        {
          var word1 = ocrLine.Words[i];
          var word2 = ocrLine.Words[i + 1];
          double horizontalGap = word2.BoundingRect.Left - word1.BoundingRect.Right;
          if (splitThreshold > 0 && horizontalGap > splitThreshold)
          {
            if (currentWordGroup.Any())
            {
              expandedLines.Add(new ProcessedLine(currentWordGroup.ToList()));
            }
            currentWordGroup = new List<OcrWord> { word2 };
          }
          else
          {
            currentWordGroup.Add(word2);
          }
        }
        if (currentWordGroup.Any())
        {
          expandedLines.Add(new ProcessedLine(currentWordGroup.ToList()));
        }
      }
      return expandedLines;
    }
    private double GetAverageWordHeight(OcrLine line)
    {
      if (line.Words == null || !line.Words.Any())
      {
        return 0;
      }
      var validWords = line.Words.Where(w => w.BoundingRect.Height > 0).ToList();
      if (validWords.Count == 0)
      {
        return 0;
      }
      return validWords.Average(word => word.BoundingRect.Height);
    }
  }
}
