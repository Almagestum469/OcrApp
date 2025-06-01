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

    private async Task<List<string>> GroupLinesIntoParagraphs(IReadOnlyList<OcrLine> originalOcrLines)
    {
      if (originalOcrLines == null || !originalOcrLines.Any())
      {
        return new List<string> { "未识别到文本" };
      }
      var processedLines = ExpandOcrLines(originalOcrLines, GetAverageWordHeight);
      if (processedLines == null || !processedLines.Any())
      {
        return new List<string> { "未识别到文本" };
      }
      var paragraphs = new List<string>();
      var currentParagraphBuilder = new StringBuilder();
      currentParagraphBuilder.Append(processedLines[0].Text);
      var previousLineRect = processedLines[0].BoundingRect;
      double previousLineAvgWordHeight = processedLines[0].AverageWordHeight;
      for (int i = 1; i < processedLines.Count; i++)
      {
        var currentLine = processedLines[i];
        var currentLineRect = currentLine.BoundingRect;
        double currentLineAvgWordHeight = currentLine.AverageWordHeight;
        double verticalGap = currentLineRect.Top - previousLineRect.Bottom;
        double paragraphBreakThresholdBaseHeight = previousLineRect.Height > 0 ? previousLineRect.Height : 10.0;
        double paragraphBreakThreshold = paragraphBreakThresholdBaseHeight * 0.75;
        paragraphBreakThreshold = Math.Max(paragraphBreakThreshold, 5.0);
        bool significantFontDifference = false;
        if (previousLineAvgWordHeight > 0.1 && currentLineAvgWordHeight > 0.1)
        {
          double maxH = Math.Max(previousLineAvgWordHeight, currentLineAvgWordHeight);
          double minH = Math.Min(previousLineAvgWordHeight, currentLineAvgWordHeight);
          double FONT_SIZE_RATIO_THRESHOLD = 1.4;
          double FONT_SIZE_ABSOLUTE_DIFF_THRESHOLD = 2.0;
          if (minH > 0 && (maxH / minH > FONT_SIZE_RATIO_THRESHOLD) && (maxH - minH > FONT_SIZE_ABSOLUTE_DIFF_THRESHOLD))
          {
            significantFontDifference = true;
          }
        }
        bool isNewParagraph = false;
        if (verticalGap > paragraphBreakThreshold)
        {
          isNewParagraph = true;
        }
        else if (significantFontDifference)
        {
          isNewParagraph = true;
        }
        if (isNewParagraph)
        {
          paragraphs.Add(currentParagraphBuilder.ToString());
          currentParagraphBuilder.Clear();
          currentParagraphBuilder.Append(currentLine.Text);
        }
        else
        {
          currentParagraphBuilder.Append(" ").Append(currentLine.Text);
        }
        previousLineRect = currentLineRect;
        previousLineAvgWordHeight = currentLineAvgWordHeight;
      }
      if (currentParagraphBuilder.Length > 0)
      {
        paragraphs.Add(currentParagraphBuilder.ToString());
      }
      var finalParagraphs = new List<string>();
      if (paragraphs.Any())
      {
        foreach (var para in paragraphs)
        {
          finalParagraphs.Add(await TranslateParagraphAsync(para));
        }
      }
      if (!finalParagraphs.Any() && processedLines.Any())
      {
        var allText = string.Join(" ", processedLines.Select(l => l.Text));
        if (!string.IsNullOrWhiteSpace(allText))
        {
          string translatedSingleParagraph = await TranslateParagraphAsync(allText);
          finalParagraphs.Add(translatedSingleParagraph);
        }
      }
      if (finalParagraphs.Count == 0)
      {
        return new List<string> { "未能将文本组合成段落" };
      }
      return finalParagraphs;
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
    private async Task<string> TranslateParagraphAsync(string paragraph)
    {
      // 暂时不进行翻译，直接返回原文
      return await Task.FromResult(paragraph);
      /*
      // 以下代码保留用于将来的翻译功能
      if (string.IsNullOrWhiteSpace(paragraph))
      {
        return paragraph;
      }
      string[] words = paragraph.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
      if (words.Length > 3)
      {
        try
        {
          string translatedText = await GoogleTranslator.TranslateEnglishToChineseAsync(paragraph);
          if (!string.IsNullOrWhiteSpace(translatedText) && !translatedText.StartsWith("Error:"))
          {
            return translatedText;
          }
          else if (translatedText.StartsWith("Error:"))
          {
            Console.WriteLine($"GoogleTranslator returned an error for paragraph: '{paragraph}'. Error: {translatedText}");
            return paragraph;
          }
          return paragraph;
        }
        catch (Exception ex)
        {
          Console.Error.WriteLine($"Exception during paragraph translation for '{paragraph}': {ex.Message}");
          return paragraph;
        }
      }
      return paragraph;
      */
    }
  }
}
