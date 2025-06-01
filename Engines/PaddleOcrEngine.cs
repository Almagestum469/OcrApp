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
    private PaddleOcrResult? _lastOcrResult;    // 段落分组算法参数 - 简化配置
    /// <summary>
    /// 垂直方向段落分割阈值倍数，相对于平均字符高度
    /// 当两个文本区域的垂直距离超过 averageHeight * 此倍数时，认为是不同段落
    /// </summary>
    private double _verticalBreakThresholdMultiplier = 1.5;

    /// <summary>
    /// 水平重合度阈值，判断两个区域在水平方向是否足够重合
    /// 值越大要求重合度越高，0.0表示不重合，1.0表示完全重合
    /// </summary>
    private double _horizontalOverlapThreshold = 0.3;

    /// <summary>
    /// OCR置信度阈值，低于此值的文本区域将被过滤
    /// </summary>
    private double _confidenceThreshold = 0.9;

    public Task<bool> InitializeAsync()
    {
      try
      {
        if (_isInitialized && _paddleOcrEngine != null)
          return Task.FromResult(true);
        FullOcrModel model = LocalFullModels.EnglishV4;
        _paddleOcrEngine = new PaddleOcrAll(model, PaddleDevice.Mkldnn())
        {
          AllowRotateDetection = false,
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
        var recognizedTexts = new List<string>(); if (result.Regions != null && result.Regions.Any())
        {
          var filteredRegions = result.Regions.Where(r => r.Score >= _confidenceThreshold).ToList();
          if (!filteredRegions.Any())
          {
            recognizedTexts.Add("未识别到满足置信度要求的文本");
            return recognizedTexts;
          }
          var sortedRegions = filteredRegions
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
      debugInfo.AppendLine($"置信度阈值: {_confidenceThreshold:F2}");

      if (_lastOcrResult == null)
      {
        debugInfo.AppendLine("无识别结果数据");
        debugInfo.AppendLine("=== 调试信息结束 ===");
        return debugInfo.ToString();
      }

      var allRegionCount = _lastOcrResult.Regions?.Count() ?? 0;
      var filteredRegions = _lastOcrResult.Regions?.Where(r => r.Score >= _confidenceThreshold).ToList() ?? new List<PaddleOcrResultRegion>();
      var filteredCount = filteredRegions.Count;
      var lowConfidenceCount = allRegionCount - filteredCount;

      debugInfo.AppendLine($"总识别区域数量: {allRegionCount}");
      debugInfo.AppendLine($"满足置信度要求的区域数量: {filteredCount}");
      if (lowConfidenceCount > 0)
      {
        debugInfo.AppendLine($"低置信度被过滤的区域数量: {lowConfidenceCount}");
      }
      debugInfo.AppendLine();

      if (filteredRegions.Any())
      {
        for (int regionIndex = 0; regionIndex < filteredRegions.Count; regionIndex++)
        {
          var region = filteredRegions[regionIndex];
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
        debugInfo.AppendLine("未检测到满足置信度要求的文本区域");
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

      // 计算平均高度用于垂直距离判断
      double averageHeight = regions.Average(r => r.Rect.Size.Height);
      double verticalBreakThreshold = averageHeight * _verticalBreakThresholdMultiplier;

      System.Diagnostics.Debug.WriteLine($"段落分组参数:");
      System.Diagnostics.Debug.WriteLine($"  平均高度: {averageHeight:F1}, 垂直阈值: {verticalBreakThreshold:F1}");
      System.Diagnostics.Debug.WriteLine($"  水平重合阈值: {_horizontalOverlapThreshold}");

      // 创建段落组
      var paragraphGroups = new List<List<PaddleOcrResultRegion>>();

      foreach (var region in regions)
      {
        bool addedToExistingGroup = false;

        // 尝试将当前区域添加到现有段落组
        foreach (var group in paragraphGroups)
        {
          // 检查是否可以与组中的任何区域合并
          if (CanMergeWithGroup(region, group, verticalBreakThreshold))
          {
            group.Add(region);
            addedToExistingGroup = true;
            System.Diagnostics.Debug.WriteLine($"区域 \"{region.Text}\" 添加到现有段落组");
            break;
          }
        }

        // 如果无法添加到现有组，创建新组
        if (!addedToExistingGroup)
        {
          paragraphGroups.Add(new List<PaddleOcrResultRegion> { region });
          System.Diagnostics.Debug.WriteLine($"区域 \"{region.Text}\" 创建新段落组");
        }
      }

      // 将每个组转换为段落文本
      foreach (var group in paragraphGroups)
      {
        // 按Y坐标排序，然后按X坐标排序
        var sortedGroup = group
          .OrderBy(r => r.Rect.Center.Y)
          .ThenBy(r => r.Rect.Center.X)
          .ToList();

        var paragraphText = string.Join(" ", sortedGroup.Select(r => r.Text));
        if (!string.IsNullOrWhiteSpace(paragraphText))
        {
          var translatedText = await TranslateParagraphAsync(paragraphText);
          paragraphs.Add(translatedText);
        }
      }

      System.Diagnostics.Debug.WriteLine($"最终生成 {paragraphs.Count} 个段落");
      return paragraphs.Any() ? paragraphs : new List<string> { "未能将文本组合成段落" };
    }

    /// <summary>
    /// 判断一个区域是否可以与现有段落组合并
    /// </summary>
    private bool CanMergeWithGroup(PaddleOcrResultRegion region, List<PaddleOcrResultRegion> group, double verticalThreshold)
    {
      foreach (var groupRegion in group)
      {
        // 检查垂直距离
        double verticalGap = Math.Abs(region.Rect.Center.Y - groupRegion.Rect.Center.Y);
        if (verticalGap > verticalThreshold)
        {
          continue; // 垂直距离太远，检查下一个
        }

        // 检查水平重合度
        double horizontalOverlap = CalculateHorizontalOverlap(region.Rect, groupRegion.Rect);
        if (horizontalOverlap >= _horizontalOverlapThreshold)
        {
          System.Diagnostics.Debug.WriteLine($"区域可合并: 垂直距离={verticalGap:F1}, 水平重合度={horizontalOverlap:F3}");
          return true;
        }
      }

      return false;
    }

    /// <summary>
    /// 计算两个矩形的水平重合度
    /// </summary>
    /// <param name="rect1">第一个矩形</param>
    /// <param name="rect2">第二个矩形</param>
    /// <returns>重合度，0.0表示不重合，1.0表示完全重合</returns>
    private double CalculateHorizontalOverlap(RotatedRect rect1, RotatedRect rect2)
    {
      // 计算水平方向的边界
      double left1 = rect1.Center.X - rect1.Size.Width / 2;
      double right1 = rect1.Center.X + rect1.Size.Width / 2;
      double left2 = rect2.Center.X - rect2.Size.Width / 2;
      double right2 = rect2.Center.X + rect2.Size.Width / 2;

      // 计算重合区间
      double overlapStart = Math.Max(left1, left2);
      double overlapEnd = Math.Min(right1, right2);
      double overlapWidth = Math.Max(0, overlapEnd - overlapStart);

      // 计算总宽度（两个矩形的并集宽度）
      double totalWidth = Math.Max(right1, right2) - Math.Min(left1, left2);

      // 返回重合度
      return totalWidth > 0 ? overlapWidth / totalWidth : 0.0;
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
      }      return paragraph;
      */
    }    /// <summary>
         /// 设置段落分组算法参数，用于调试和优化
         /// </summary>
         /// <param name="verticalMultiplier">垂直方向阈值倍数</param>
         /// <param name="horizontalOverlapThreshold">水平重合度阈值</param>
         /// <param name="confidenceThreshold">置信度阈值</param>
    public void SetParagraphGroupingParameters(
      double verticalMultiplier = 1.5,
      double horizontalOverlapThreshold = 0.3,
      double confidenceThreshold = 0.9)
    {
      _verticalBreakThresholdMultiplier = verticalMultiplier;
      _horizontalOverlapThreshold = horizontalOverlapThreshold;
      _confidenceThreshold = confidenceThreshold;

      System.Diagnostics.Debug.WriteLine("段落分组参数已更新:");
      System.Diagnostics.Debug.WriteLine($"  垂直阈值倍数: {_verticalBreakThresholdMultiplier}");
      System.Diagnostics.Debug.WriteLine($"  水平重合度阈值: {_horizontalOverlapThreshold}");
      System.Diagnostics.Debug.WriteLine($"  置信度阈值: {_confidenceThreshold}");
    }

    /// <summary>
    /// 获取当前段落分组算法参数
    /// </summary>
    /// <returns>参数信息字符串</returns>
    public string GetParagraphGroupingParameters()
    {
      return $"段落分组参数:\n" +
             $"  垂直阈值倍数: {_verticalBreakThresholdMultiplier}\n" +
             $"  水平重合度阈值: {_horizontalOverlapThreshold}\n" +
             $"  置信度阈值: {_confidenceThreshold}";
    }

    public void Dispose()
    {
      _paddleOcrEngine?.Dispose();
      _paddleOcrEngine = null;
      _isInitialized = false;
    }
  }
}
