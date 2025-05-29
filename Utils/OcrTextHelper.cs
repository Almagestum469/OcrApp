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
    public static List<string> GroupLinesIntoParagraphs(IReadOnlyList<OcrLine> ocrLines)
    {
      if (ocrLines == null || !ocrLines.Any())
      {
        return ["未识别到文本"];
      }

      var paragraphs = new List<string>();

      var currentParagraphBuilder = new StringBuilder();
      currentParagraphBuilder.Append(ocrLines[0].Text);
      Rect previousLineRect = GetLineBoundingRect(ocrLines[0]);
      double previousLineAvgWordHeight = GetAverageWordHeight(ocrLines[0]);

      for (int i = 1; i < ocrLines.Count; i++)
      {
        var currentLine = ocrLines[i];
        Rect currentLineRect = GetLineBoundingRect(currentLine);
        double currentLineAvgWordHeight = GetAverageWordHeight(currentLine);

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

      if (!paragraphs.Any() && ocrLines.Any())
      {
        var allText = string.Join(" ", ocrLines.Select(l => l.Text));
        if (!string.IsNullOrWhiteSpace(allText))
        {
          paragraphs.Add(allText);
        }
      }

      if (paragraphs.Count == 0)
      {
        return new List<string> { "未能将文本组合成段落" };
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
      if (!validWords.Any())
      {
        return 0;
      }
      return validWords.Average(word => word.BoundingRect.Height);
    }

    private static Rect GetLineBoundingRect(OcrLine line)
    {
      if (line.Words == null || !line.Words.Any())
      {
        return new Rect(); // Return empty Rect if no words
      }

      double minX = line.Words.Min(word => word.BoundingRect.Left);
      double minY = line.Words.Min(word => word.BoundingRect.Top);
      double maxX = line.Words.Max(word => word.BoundingRect.Right);
      double maxY = line.Words.Max(word => word.BoundingRect.Bottom);

      return new Rect(minX, minY, maxX - minX, maxY - minY);
    }
  }
}
