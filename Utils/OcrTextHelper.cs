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
        return new List<string> { "未识别到文本" };
      }

      var paragraphs = new List<string>();
      if (!ocrLines.Any()) return paragraphs;

      var currentParagraphBuilder = new StringBuilder();
      currentParagraphBuilder.Append(ocrLines[0].Text);
      Rect previousLineRect = GetLineBoundingRect(ocrLines[0]);

      for (int i = 1; i < ocrLines.Count; i++)
      {
        var currentLine = ocrLines[i];
        Rect currentLineRect = GetLineBoundingRect(currentLine);

        double verticalGap = currentLineRect.Top - previousLineRect.Bottom;
        double paragraphBreakThreshold = previousLineRect.Height * 0.75;
        paragraphBreakThreshold = Math.Max(paragraphBreakThreshold, 5.0);

        if (verticalGap > paragraphBreakThreshold)
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

      if (!paragraphs.Any())
      {
        return new List<string> { "未能将文本组合成段落" };
      }

      return paragraphs;
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
