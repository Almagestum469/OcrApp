using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Windows.Foundation;
using Windows.Media.Ocr;

namespace OcrApp.Utils
{
  public static class OcrTextHelper
  {
    private class ProcessedLine
    {
      public string Text { get; }
      public IReadOnlyList<OcrWord> Words { get; }
      public Rect BoundingRect { get; }
      public double AverageWordHeight { get; }

      public ProcessedLine(IReadOnlyList<OcrWord> words)
      {
        Words = words ?? new List<OcrWord>();
        Text = string.Join(" ", Words.Select(w => w.Text));
        BoundingRect = CalculateBoundingRectForWords(Words);
        AverageWordHeight = CalculateAverageWordHeightForWords(Words);
      }

      private static Rect CalculateBoundingRectForWords(IReadOnlyList<OcrWord> words)
      {
        if (words == null || !words.Any()) return new Rect(0, 0, 0, 0);

        double minX = words.Min(word => word.BoundingRect.Left);
        double minY = words.Min(word => word.BoundingRect.Top);
        double maxX = words.Max(word => word.BoundingRect.Right);
        double maxY = words.Max(word => word.BoundingRect.Bottom);
        return new Rect(minX, minY, Math.Max(0, maxX - minX), Math.Max(0, maxY - minY));
      }

      private static double CalculateAverageWordHeightForWords(IReadOnlyList<OcrWord> words)
      {
        if (words == null || !words.Any()) return 0;
        var validWords = words.Where(w => w.BoundingRect.Height > 0).ToList();
        if (!validWords.Any()) return 0;
        return validWords.Average(word => word.BoundingRect.Height);
      }
    }

    private static List<ProcessedLine> ExpandOcrLines(IReadOnlyList<OcrLine> ocrLines, Func<OcrLine, double> getAvgWordHeightFunc)
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

    public static List<string> GroupLinesIntoParagraphs(IReadOnlyList<OcrLine> originalOcrLines)
    {
      if (originalOcrLines == null || !originalOcrLines.Any())
      {
        return ["未识别到文本"];
      }

      // Pass GetAverageWordHeight (the existing static method) as a delegate
      var processedLines = ExpandOcrLines(originalOcrLines, GetAverageWordHeight);

      if (processedLines == null || !processedLines.Any())
      {
        return ["未识别到文本"];
      }

      var paragraphs = new List<string>();

      var currentParagraphBuilder = new StringBuilder();
      currentParagraphBuilder.Append(processedLines[0].Text);
      Rect previousLineRect = processedLines[0].BoundingRect;
      double previousLineAvgWordHeight = processedLines[0].AverageWordHeight;

      for (int i = 1; i < processedLines.Count; i++)
      {
        var currentLine = processedLines[i];
        Rect currentLineRect = currentLine.BoundingRect;
        double currentLineAvgWordHeight = currentLine.AverageWordHeight;

        double verticalGap = currentLineRect.Top - previousLineRect.Bottom;
        // Ensure previousLineRect.Height is used from ProcessedLine, which should be non-negative
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

      if (!paragraphs.Any() && processedLines.Any())
      {
        var allText = string.Join(" ", processedLines.Select(l => l.Text));
        if (!string.IsNullOrWhiteSpace(allText))
        {
          paragraphs.Add(allText);
        }
      }

      if (paragraphs.Count == 0)
      {
        return ["未能将文本组合成段落"];
      }

      return paragraphs;
    }

    private static double GetAverageWordHeight(OcrLine line)
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
