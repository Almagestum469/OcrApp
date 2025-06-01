using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;
using OpenCvSharp;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Local;
using Sdcb.PaddleInference;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;

namespace OcrApp.Utils
{
  public static class PaddleOcrHelper
  {
    private static PaddleOcrAll? _paddleOcrEngine;
    private static bool _isInitialized = false;
    private static PaddleOcrResult? _lastOcrResult = null;/// <summary>
                                                          /// 初始化PaddleOCR引擎
                                                          /// </summary>
    public static Task<bool> InitializeAsync()
    {
      try
      {
        if (_isInitialized && _paddleOcrEngine != null)
          return Task.FromResult(true);

        // 使用英文模型
        FullOcrModel model = LocalFullModels.EnglishV4;

        // 创建PaddleOCR引擎，使用MKL CPU优化
        _paddleOcrEngine = new PaddleOcrAll(model, PaddleDevice.Mkldnn())
        {
          AllowRotateDetection = true, // 允许识别有角度的文字
          Enable180Classification = false, // 不允许识别旋转角度大于90度的文字
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

    /// <summary>
    /// 使用PaddleOCR识别SoftwareBitmap中的文字
    /// </summary>
    /// <param name="bitmap">要识别的位图</param>
    /// <returns>识别结果列表</returns>
    public static async Task<List<string>> RecognizeTextAsync(SoftwareBitmap bitmap)
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
        // 将SoftwareBitmap转换为字节数组
        var imageBytes = await ConvertSoftwareBitmapToBytesAsync(bitmap);

        // 使用OpenCV解码图像
        using var mat = Cv2.ImDecode(imageBytes, ImreadModes.Color);        // 执行OCR识别
        var result = _paddleOcrEngine!.Run(mat);
        _lastOcrResult = result; // 保存最后的识别结果

        // 处理识别结果
        var recognizedTexts = new List<string>();

        if (result.Regions != null && result.Regions.Any())
        {
          // 按照位置排序（从上到下，从左到右）
          var sortedRegions = result.Regions
              .OrderBy(r => r.Rect.Center.Y)
              .ThenBy(r => r.Rect.Center.X)
              .ToList();

          // 将相近的行合并为段落
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
    }    /// <summary>
         /// 将SoftwareBitmap转换为字节数组
         /// </summary>
    private static async Task<byte[]> ConvertSoftwareBitmapToBytesAsync(SoftwareBitmap bitmap)
    {
      using var stream = new InMemoryRandomAccessStream();

      // 创建编码器
      var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
      encoder.SetSoftwareBitmap(bitmap);

      await encoder.FlushAsync();

      // 转换为字节数组
      var bytes = new byte[stream.Size];
      var buffer = bytes.AsBuffer();
      await stream.ReadAsync(buffer, (uint)stream.Size, InputStreamOptions.None);

      return bytes;
    }

    /// <summary>
    /// 将识别的区域按位置组合成段落
    /// </summary>
    private static async Task<List<string>> GroupRegionsIntoParagraphs(List<PaddleOcrResultRegion> regions)
    {
      if (!regions.Any())
        return new List<string> { "未识别到文本" };

      var paragraphs = new List<string>();
      var currentParagraph = new List<string>();

      double previousY = regions[0].Rect.Center.Y;
      double averageHeight = regions.Average(r => r.Rect.Size.Height);
      double paragraphBreakThreshold = averageHeight * 1.5; // 行间距超过1.5倍行高时分段

      foreach (var region in regions)
      {
        double currentY = region.Rect.Center.Y;
        double verticalGap = Math.Abs(currentY - previousY);

        // 如果垂直间距较大，开始新段落
        if (verticalGap > paragraphBreakThreshold && currentParagraph.Any())
        {
          var paragraphText = string.Join(" ", currentParagraph);
          if (!string.IsNullOrWhiteSpace(paragraphText))
          {
            // 翻译段落（如果需要）
            var translatedText = await TranslateParagraphAsync(paragraphText);
            paragraphs.Add(translatedText);
          }
          currentParagraph.Clear();
        }

        currentParagraph.Add(region.Text);
        previousY = currentY;
      }

      // 处理最后一个段落
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
    }    /// <summary>
         /// 翻译段落文本
         /// </summary>
    private static async Task<string> TranslateParagraphAsync(string paragraph)
    {
      // 暂时不进行翻译，直接返回原文
      return await Task.FromResult(paragraph);

      // 以下代码保留用于将来的翻译功能
      /*
      if (string.IsNullOrWhiteSpace(paragraph))
        return paragraph;

      // 如果文本包含超过3个单词且主要是英文，则进行翻译
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

      return paragraph; // 返回原文
      */
    }/// <summary>
     /// 生成PaddleOCR调试信息
     /// </summary>
     /// <returns>详细的调试信息字符串</returns>
    public static string GenerateDebugInfo()
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

          // 输出边界框信息
          debugInfo.AppendLine($"边界框中心: X={region.Rect.Center.X:F1}, Y={region.Rect.Center.Y:F1}");
          debugInfo.AppendLine($"边界框大小: W={region.Rect.Size.Width:F1}, H={region.Rect.Size.Height:F1}");
          debugInfo.AppendLine($"旋转角度: {region.Rect.Angle:F2}°");

          // 输出四个角点坐标
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
    }    /// <summary>
         /// 安全地获取旋转矩形的角点坐标
         /// </summary>
    private static object[] GetRectPoints(OpenCvSharp.RotatedRect rect)
    {
      try
      {
        var points = rect.Points();
        return points?.Select(p => new { X = p.X, Y = p.Y }).ToArray() ?? new object[0];
      }
      catch
      {
        return new object[0];
      }
    }

    /// <summary>
    /// 释放PaddleOCR资源
    /// </summary>
    public static void Dispose()
    {
      _paddleOcrEngine?.Dispose();
      _paddleOcrEngine = null;
      _isInitialized = false;
    }
  }
}
