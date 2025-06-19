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

    /// <summary>
    /// 缓存每个区域基于角点计算的实际边界框信息
    /// </summary>
    private Dictionary<PaddleOcrResultRegion, (double Left, double Right, double Top, double Bottom, double ActualWidth, double ActualHeight)> _regionBoundsCache = new Dictionary<PaddleOcrResultRegion, (double, double, double, double, double, double)>();// 段落分组算法参数 - 简化配置
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
    private double _confidenceThreshold = 0.90;

    /// <summary>
    /// 行高差异阈值倍数，用于判断两行是否因高度差异过大而属于不同段落。
    /// 例如，1.5 表示如果一个区域的高度是另一个区域的1.5倍以上（或反之），则视为不同段落的组成部分。
    /// </summary>
    private double _heightDifferenceThresholdMultiplier = 1.5;

    public Task<bool> InitializeAsync()
    {
      try
      {
        if (_isInitialized && _paddleOcrEngine != null)
          return Task.FromResult(true);
        FullOcrModel model = LocalFullModels.EnglishV4;
        _paddleOcrEngine = new PaddleOcrAll(model, PaddleDevice.Onnx())
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
        // 清空边界框缓存，确保使用最新的识别结果
        _regionBoundsCache.Clear();

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
          }          // 使用基于角点的实际边界框进行排序
          var sortedRegions = filteredRegions
              .OrderBy(r =>
              {
                var bounds = GetActualBounds(r);
                return (bounds.Top + bounds.Bottom) / 2.0; // 使用实际的Y中心
              })
              .ThenBy(r =>
              {
                var bounds = GetActualBounds(r);
                return (bounds.Left + bounds.Right) / 2.0; // 使用实际的X中心
              })
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

              // 显示基于角点计算的实际边界框信息
              var actualBounds = GetActualBounds(region);
              debugInfo.AppendLine("基于角点的实际边界框:");
              debugInfo.AppendLine($"  实际宽高: {actualBounds.ActualWidth:F1} x {actualBounds.ActualHeight:F1}");
              debugInfo.AppendLine($"  实际边界: 左={actualBounds.Left:F1}, 右={actualBounds.Right:F1}, 上={actualBounds.Top:F1}, 下={actualBounds.Bottom:F1}");
              debugInfo.AppendLine($"  实际中心: X={(actualBounds.Left + actualBounds.Right) / 2.0:F1}, Y={(actualBounds.Top + actualBounds.Bottom) / 2.0:F1}");

              // 比较原始和修正后的尺寸
              if (Math.Abs(region.Rect.Size.Width - actualBounds.ActualWidth) > 1.0 ||
                  Math.Abs(region.Rect.Size.Height - actualBounds.ActualHeight) > 1.0)
              {
                debugInfo.AppendLine($"  ⚠️ 检测到宽高错误: 原始({region.Rect.Size.Width:F1}x{region.Rect.Size.Height:F1}) vs 实际({actualBounds.ActualWidth:F1}x{actualBounds.ActualHeight:F1})");
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

      System.Diagnostics.Debug.WriteLine($"段落分组参数:");
      System.Diagnostics.Debug.WriteLine($"  垂直阈值倍数: {_verticalBreakThresholdMultiplier}");
      System.Diagnostics.Debug.WriteLine($"  水平重合阈值: {_horizontalOverlapThreshold}");
      System.Diagnostics.Debug.WriteLine($"  行高差异阈值倍数: {_heightDifferenceThresholdMultiplier}"); // 输出新参数

      // 创建段落组
      var paragraphGroups = new List<List<PaddleOcrResultRegion>>();

      foreach (var region in regions)
      {
        bool addedToExistingGroup = false;

        // 尝试将当前区域添加到现有段落组
        foreach (var group in paragraphGroups)
        {
          // 检查是否可以与组中的任何区域合并
          if (CanMergeWithGroup(region, group))
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
      {        // 使用基于角点的实际边界框进行排序
        var sortedGroup = group
          .OrderBy(r =>
          {
            var bounds = GetActualBounds(r);
            return (bounds.Top + bounds.Bottom) / 2.0; // 使用实际的Y中心
          })
          .ThenBy(r =>
          {
            var bounds = GetActualBounds(r);
            return (bounds.Left + bounds.Right) / 2.0; // 使用实际的X中心
          })
          .ToList();

        var paragraphText = string.Join(" ", sortedGroup.Select(r => r.Text));
        if (!string.IsNullOrWhiteSpace(paragraphText))
        {
          paragraphs.Add(paragraphText); // 直接添加原文，不再翻译
        }
      }

      System.Diagnostics.Debug.WriteLine($"最终生成 {paragraphs.Count} 个段落");
      return paragraphs.Any() ? paragraphs : new List<string> { "未能将文本组合成段落" };
    }    /// <summary>
         /// 判断一个区域是否可以与现有段落组合并
         /// </summary>
    private bool CanMergeWithGroup(PaddleOcrResultRegion region, List<PaddleOcrResultRegion> group)
    {
      foreach (var groupRegion in group)
      {        // 使用基于角点计算的实际高度作为垂直距离判断基准
        double averageHeight = GetAverageHeight(region, groupRegion); // Calls GetActualBounds internally (cached)
        double verticalThreshold = averageHeight * _verticalBreakThresholdMultiplier;

        // 使用基于角点计算的垂直距离
        double verticalGap = GetVerticalDistance(region, groupRegion); // Calls GetActualBounds internally (cached)
        if (verticalGap > verticalThreshold)
        {
          System.Diagnostics.Debug.WriteLine($"区域 '{region.Text}' 与 '{groupRegion.Text}': 垂直距离 ({verticalGap:F1}) > 阈值 ({verticalThreshold:F1} 基于平均高度 {averageHeight:F1}). 跳过此配对.");
          continue; // 垂直距离太远，检查下一个 groupRegion
        }

        // 新增：检查行高差异
        var boundsRegion = GetActualBounds(region);
        var boundsGroupRegion = GetActualBounds(groupRegion);
        double heightRegion = boundsRegion.ActualHeight;
        double heightGroupRegion = boundsGroupRegion.ActualHeight;

        bool significantHeightDifference = false;
        // 仅当两个高度都为正数时才进行比例比较
        if (heightRegion > 0 && heightGroupRegion > 0)
        {
          double largerHeight = Math.Max(heightRegion, heightGroupRegion);
          double smallerHeight = Math.Min(heightRegion, heightGroupRegion);
          if (largerHeight > smallerHeight * _heightDifferenceThresholdMultiplier)
          {
            significantHeightDifference = true;
          }
        }
        // 如果一个高度有效而另一个无效（0或负），也视为显著差异
        else if ((heightRegion <= 0 && heightGroupRegion > 0) || (heightGroupRegion <= 0 && heightRegion > 0))
        {
          significantHeightDifference = true;
        }
        // 如果两个高度都无效 (<=0), significantHeightDifference 保持 false, 此检查不阻止合并

        if (significantHeightDifference)
        {
          System.Diagnostics.Debug.WriteLine($"区域 '{region.Text}' (H:{heightRegion:F1}) 与 '{groupRegion.Text}' (H:{heightGroupRegion:F1}): 高度差异过大 (阈值倍数: {_heightDifferenceThresholdMultiplier}). 跳过此配对.");
          continue; // 高度差异过大，检查下一个 groupRegion
        }

        // 检查水平重合度（使用新的基于角点的计算）
        double horizontalOverlap = CalculateHorizontalOverlapByBounds(region, groupRegion); // Calls GetActualBounds internally (cached)
        if (horizontalOverlap >= _horizontalOverlapThreshold)
        {
          System.Diagnostics.Debug.WriteLine($"区域可合并: '{region.Text}' 与 '{groupRegion.Text}'. 垂直距离={verticalGap:F1} (阈值 {verticalThreshold:F1}), 高度相似, 水平重合度={horizontalOverlap:F3} (阈值 {_horizontalOverlapThreshold:F3})");
          return true; // Found a suitable region in the group to merge with
        }
        else
        {
          System.Diagnostics.Debug.WriteLine($"区域 '{region.Text}' 与 '{groupRegion.Text}': 水平重合度 ({horizontalOverlap:F3}) < 阈值 ({_horizontalOverlapThreshold:F3}). 跳过此配对.");
          // Loop continues to the next groupRegion
        }
      }
      return false; // No region in the group could be merged with
    }

    /// <summary>
    /// 基于四个角点坐标计算实际的边界框信息
    /// </summary>
    /// <param name="region">OCR识别区域</param>
    /// <returns>实际边界框信息：左，右，上，下，宽度，高度</returns>
    private (double Left, double Right, double Top, double Bottom, double ActualWidth, double ActualHeight) GetActualBounds(PaddleOcrResultRegion region)
    {
      // 检查缓存
      if (_regionBoundsCache.TryGetValue(region, out var cachedBounds))
      {
        return cachedBounds;
      }

      try
      {
        var points = region.Rect.Points();
        if (points == null || points.Length < 4)
        {
          // 如果无法获取角点，回退到原始方法
          double fallbackLeft = region.Rect.Center.X - region.Rect.Size.Width / 2;
          double fallbackRight = region.Rect.Center.X + region.Rect.Size.Width / 2;
          double fallbackTop = region.Rect.Center.Y - region.Rect.Size.Height / 2;
          double fallbackBottom = region.Rect.Center.Y + region.Rect.Size.Height / 2;
          var fallbackBounds = (fallbackLeft, fallbackRight, fallbackTop, fallbackBottom,
                               region.Rect.Size.Width, region.Rect.Size.Height);
          _regionBoundsCache[region] = fallbackBounds;
          return fallbackBounds;
        }

        // 基于四个角点计算实际边界
        double minX = points.Min(p => p.X);
        double maxX = points.Max(p => p.X);
        double minY = points.Min(p => p.Y);
        double maxY = points.Max(p => p.Y);

        double actualWidth = maxX - minX;
        double actualHeight = maxY - minY;

        var actualBounds = (minX, maxX, minY, maxY, actualWidth, actualHeight);
        _regionBoundsCache[region] = actualBounds;

        System.Diagnostics.Debug.WriteLine($"区域 \"{region.Text}\" 边界框修正:");
        System.Diagnostics.Debug.WriteLine($"  原始宽高: {region.Rect.Size.Width:F1} x {region.Rect.Size.Height:F1}");
        System.Diagnostics.Debug.WriteLine($"  实际宽高: {actualWidth:F1} x {actualHeight:F1}");
        System.Diagnostics.Debug.WriteLine($"  原始中心: ({region.Rect.Center.X:F1}, {region.Rect.Center.Y:F1})");
        System.Diagnostics.Debug.WriteLine($"  实际边界: 左={minX:F1}, 右={maxX:F1}, 上={minY:F1}, 下={maxY:F1}");

        return actualBounds;
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"计算实际边界框失败: {ex.Message}，使用原始方法");
        // 出错时回退到原始方法
        double fallbackLeft = region.Rect.Center.X - region.Rect.Size.Width / 2;
        double fallbackRight = region.Rect.Center.X + region.Rect.Size.Width / 2;
        double fallbackTop = region.Rect.Center.Y - region.Rect.Size.Height / 2;
        double fallbackBottom = region.Rect.Center.Y + region.Rect.Size.Height / 2;
        var fallbackBounds = (fallbackLeft, fallbackRight, fallbackTop, fallbackBottom,
                             region.Rect.Size.Width, region.Rect.Size.Height);
        _regionBoundsCache[region] = fallbackBounds;
        return fallbackBounds;
      }
    }

    /// <summary>
    /// 基于实际边界框计算两个区域的平均高度
    /// </summary>
    private double GetAverageHeight(PaddleOcrResultRegion region1, PaddleOcrResultRegion region2)
    {
      var bounds1 = GetActualBounds(region1);
      var bounds2 = GetActualBounds(region2);
      return (bounds1.ActualHeight + bounds2.ActualHeight) / 2.0;
    }

    /// <summary>
    /// 基于实际边界框计算两个区域的垂直距离
    /// </summary>
    private double GetVerticalDistance(PaddleOcrResultRegion region1, PaddleOcrResultRegion region2)
    {
      var bounds1 = GetActualBounds(region1);
      var bounds2 = GetActualBounds(region2);

      // 计算两个区域中心点的垂直距离
      double center1Y = (bounds1.Top + bounds1.Bottom) / 2.0;
      double center2Y = (bounds2.Top + bounds2.Bottom) / 2.0;

      return Math.Abs(center1Y - center2Y);
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
      double totalWidth = Math.Max(right1, right2) - Math.Min(left1, left2);      // 返回重合度
      return totalWidth > 0 ? overlapWidth / totalWidth : 0.0;
    }    /// <summary>
         /// 基于实际边界框计算两个区域的水平重合度
         /// </summary>
         /// <param name="region1">第一个区域</param>
         /// <param name="region2">第二个区域</param>
         /// <returns>重合度，0.0表示不重合，1.0表示完全重合</returns>
    private double CalculateHorizontalOverlapByBounds(PaddleOcrResultRegion region1, PaddleOcrResultRegion region2)
    {
      var bounds1 = GetActualBounds(region1);
      var bounds2 = GetActualBounds(region2);

      // 计算重合区间
      double overlapStart = Math.Max(bounds1.Left, bounds2.Left);
      double overlapEnd = Math.Min(bounds1.Right, bounds2.Right);
      double overlapWidth = Math.Max(0, overlapEnd - overlapStart);

      if (overlapWidth <= 0)
        return 0.0;

      // 使用相对于较小区域的重合度，这样能更好地处理短行被长行包含的情况
      double width1 = bounds1.ActualWidth;
      double width2 = bounds2.ActualWidth;
      double smallerWidth = Math.Min(width1, width2);

      // 如果较小区域的重合比例超过阈值，则认为可以合并
      // 这样即使短行只有长行的30%宽度，只要短行的大部分都与长行重合，就能归为同一段落
      double overlapRatio = smallerWidth > 0 ? overlapWidth / smallerWidth : 0.0;

      System.Diagnostics.Debug.WriteLine($"水平重合度计算: 区域1宽度={width1:F1}, 区域2宽度={width2:F1}, 重合宽度={overlapWidth:F1}, 较小宽度={smallerWidth:F1}, 重合比例={overlapRatio:F3}");

      return overlapRatio;
    }

    /// <summary>
    /// 设置段落分组算法参数，用于调试和优化
    /// </summary>
    /// <param name="verticalMultiplier">垂直方向阈值倍数</param>
    /// <param name="horizontalOverlapThreshold">水平重合度阈值</param>
    /// <param name="confidenceThreshold">置信度阈值</param>
    public void SetParagraphGroupingParameters(
      double verticalMultiplier = 1.5,
      double horizontalOverlapThreshold = 0.3,
      double confidenceThreshold = 0.9,
      double heightDifferenceMultiplier = 1.5) // 新增参数及默认值
    {
      _verticalBreakThresholdMultiplier = verticalMultiplier;
      _horizontalOverlapThreshold = horizontalOverlapThreshold;
      _confidenceThreshold = confidenceThreshold;
      _heightDifferenceThresholdMultiplier = heightDifferenceMultiplier; // 赋值新参数

      System.Diagnostics.Debug.WriteLine("段落分组参数已更新:");
      System.Diagnostics.Debug.WriteLine($"  垂直阈值倍数: {_verticalBreakThresholdMultiplier}");
      System.Diagnostics.Debug.WriteLine($"  水平重合度阈值: {_horizontalOverlapThreshold}");
      System.Diagnostics.Debug.WriteLine($"  置信度阈值: {_confidenceThreshold}");
      System.Diagnostics.Debug.WriteLine($"  行高差异阈值倍数: {_heightDifferenceThresholdMultiplier}"); // 输出新参数
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
             $"  置信度阈值: {_confidenceThreshold}\n" +
             $"  行高差异阈值倍数: {_heightDifferenceThresholdMultiplier}"; // 添加新参数到返回字符串
    }
    public void Dispose()
    {
      _paddleOcrEngine?.Dispose();
      _paddleOcrEngine = null;
      _regionBoundsCache.Clear();
      _isInitialized = false;
    }
  }
}
