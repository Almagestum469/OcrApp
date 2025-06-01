using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OpenCvSharp;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Local;
using Sdcb.PaddleInference;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;

namespace OcrApp.Engines
{
  public class PaddleOcrEngine : IOcrEngine
  {
    private PaddleOcrAll? _paddleOcrEngine;
    private bool _isInitialized = false;
    private PaddleOcrResult? _lastOcrResult;

    public Task<bool> InitializeAsync()
    {
      try
      {
        if (_isInitialized && _paddleOcrEngine != null)
          return Task.FromResult(true);
        FullOcrModel model = LocalFullModels.EnglishV4;
        _paddleOcrEngine = new PaddleOcrAll(model, PaddleDevice.Mkldnn())
        {
          AllowRotateDetection = true,
          Enable180Classification = false,
        };
        _isInitialized = true;
        return Task.FromResult(true);
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"PaddleOCR初始化失败: {ex.Message}");
        return Task.FromResult(false);
      }
    }

    public async Task<List<string>> RecognizeAsync(SoftwareBitmap bitmap)
    {
      if (!_isInitialized || _paddleOcrEngine == null)
      {
        var initResult = await InitializeAsync();
        if (!initResult)
        {
          return new List<string> { "PaddleOCR引擎初始化失败" };
        }
      }
      try
      {
        var imageBytes = await ConvertSoftwareBitmapToBytesAsync(bitmap);
        using var mat = Cv2.ImDecode(imageBytes, ImreadModes.Color);
        var result = _paddleOcrEngine!.Run(mat);
        _lastOcrResult = result;
        var recognizedTexts = new List<string>();
        if (result.Regions != null && result.Regions.Any())
        {
          var sortedRegions = result.Regions
              .OrderBy(r => r.Rect.Center.Y)
              .ThenBy(r => r.Rect.Center.X)
              .ToList();
          var paragraphs = await GroupRegionsIntoParagraphs(sortedRegions);
          recognizedTexts.AddRange(paragraphs);
        }
        else
        {
          recognizedTexts.Add("未识别到文本");
        }
        return recognizedTexts;
      }
      catch (Exception ex)
      {
        return new List<string> { $"PaddleOCR识别失败: {ex.Message}" };
      }
    }

    public string GenerateDebugInfo()
    {
      var debugInfo = new StringBuilder();
      debugInfo.AppendLine("=== PaddleOCR 识别结果详细信息 ===");
      debugInfo.AppendLine($"识别时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
      debugInfo.AppendLine($"引擎状态: {(_isInitialized ? "已初始化" : "未初始化")}");
      if (_lastOcrResult == null)
      {
        debugInfo.AppendLine("无识别结果数据");
        debugInfo.AppendLine("=== 调试信息结束 ===");
        return debugInfo.ToString();
      }
      var regionCount = _lastOcrResult.Regions?.Count() ?? 0;
      debugInfo.AppendLine($"识别区域数量: {regionCount}");
      debugInfo.AppendLine();
      if (_lastOcrResult.Regions != null && _lastOcrResult.Regions.Any())
      {
        var regionsList = _lastOcrResult.Regions.ToList();
        for (int regionIndex = 0; regionIndex < regionsList.Count; regionIndex++)
        {
          var region = regionsList[regionIndex];
          debugInfo.AppendLine($"--- 区域 {regionIndex + 1} ---");
          debugInfo.AppendLine($"文本: \"{region.Text}\"");
          debugInfo.AppendLine($"置信度: {region.Score:F4}");
          debugInfo.AppendLine($"边界框中心: X={region.Rect.Center.X:F1}, Y={region.Rect.Center.Y:F1}");
          debugInfo.AppendLine($"边界框大小: W={region.Rect.Size.Width:F1}, H={region.Rect.Size.Height:F1}");
          debugInfo.AppendLine($"旋转角度: {region.Rect.Angle:F2}°");
          try
          {
            var points = region.Rect.Points();
            if (points != null && points.Length >= 4)
            {
              debugInfo.AppendLine("四个角点坐标:");
              for (int i = 0; i < points.Length; i++)
              {
                var point = points[i];
                debugInfo.AppendLine($"  点{i + 1}: ({point.X:F1}, {point.Y:F1})");
              }
            }
          }
          catch (Exception ex)
          {
            debugInfo.AppendLine($"获取角点坐标失败: {ex.Message}");
          }
          debugInfo.AppendLine();
        }
      }
      else
      {
        debugInfo.AppendLine("未检测到任何文本区域");
      }
      debugInfo.AppendLine("=== 调试信息结束 ===");
      return debugInfo.ToString();
    }

    private async Task<byte[]> ConvertSoftwareBitmapToBytesAsync(SoftwareBitmap bitmap)
    {
      using var stream = new InMemoryRandomAccessStream();
      var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
      encoder.SetSoftwareBitmap(bitmap);
      await encoder.FlushAsync();
      var bytes = new byte[stream.Size];
      var buffer = bytes.AsBuffer();
      await stream.ReadAsync(buffer, (uint)stream.Size, InputStreamOptions.None);
      return bytes;
    }

    private async Task<List<string>> GroupRegionsIntoParagraphs(List<PaddleOcrResultRegion> regions)
    {
      if (!regions.Any())
        return new List<string> { "未识别到文本" };
      var paragraphs = new List<string>();
      var currentParagraph = new List<string>();
      double previousY = regions[0].Rect.Center.Y;
      double averageHeight = regions.Average(r => r.Rect.Size.Height);
      double paragraphBreakThreshold = averageHeight * 1.5;
      foreach (var region in regions)
      {
        double currentY = region.Rect.Center.Y;
        double verticalGap = Math.Abs(currentY - previousY);
        if (verticalGap > paragraphBreakThreshold && currentParagraph.Any())
        {
          var paragraphText = string.Join(" ", currentParagraph);
          if (!string.IsNullOrWhiteSpace(paragraphText))
          {
            var translatedText = await TranslateParagraphAsync(paragraphText);
            paragraphs.Add(translatedText);
          }
          currentParagraph.Clear();
        }
        currentParagraph.Add(region.Text);
        previousY = currentY;
      }
      if (currentParagraph.Any())
      {
        var paragraphText = string.Join(" ", currentParagraph);
        if (!string.IsNullOrWhiteSpace(paragraphText))
        {
          var translatedText = await TranslateParagraphAsync(paragraphText);
          paragraphs.Add(translatedText);
        }
      }
      return paragraphs.Any() ? paragraphs : new List<string> { "未能将文本组合成段落" };
    }

    private async Task<string> TranslateParagraphAsync(string paragraph)
    {
      // 暂时不进行翻译，直接返回原文
      return await Task.FromResult(paragraph);
      /*
      if (string.IsNullOrWhiteSpace(paragraph))
        return paragraph;
      string[] words = paragraph.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
      if (words.Length > 3)
      {
        try
        {
          var translatedText = await GoogleTranslator.TranslateEnglishToChineseAsync(paragraph);
          if (!string.IsNullOrWhiteSpace(translatedText) && !translatedText.StartsWith("Error:"))
          {
            return translatedText;
          }
          else if (translatedText.StartsWith("Error:"))
          {
            System.Diagnostics.Debug.WriteLine($"翻译错误: {translatedText}");
          }
        }
        catch (Exception ex)
        {
          System.Diagnostics.Debug.WriteLine($"翻译异常: {ex.Message}");
        }
      }
      return paragraph;
      */
    }

    public void Dispose()
    {
      _paddleOcrEngine?.Dispose();
      _paddleOcrEngine = null;
      _isInitialized = false;
    }
  }
}
