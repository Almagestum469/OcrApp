using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
    private static bool _isInitialized = false;    /// <summary>
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
        using var mat = Cv2.ImDecode(imageBytes, ImreadModes.Color);

        // 执行OCR识别
        var result = _paddleOcrEngine!.Run(mat);

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
    }

    /// <summary>
    /// 翻译段落文本
    /// </summary>
    private static async Task<string> TranslateParagraphAsync(string paragraph)
    {
      return paragraph; // do not translate for now

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
