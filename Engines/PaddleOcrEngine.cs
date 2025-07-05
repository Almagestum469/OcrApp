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
    private Dictionary<PaddleOcrResultRegion, (double Left, double Right, double Top, double Bottom, double ActualWidth, double ActualHeight)> _regionBoundsCache = new Dictionary<PaddleOcrResultRegion, (double, double, double, double, double, double)>();
    private double _confidenceThreshold = 0.90;

    // 记录最近一次识别耗时（毫秒）
    private long _lastElapsedMs = 0;

    public Task<bool> InitializeAsync()
    {
      try
      {
        if (_isInitialized && _paddleOcrEngine != null)
          return Task.FromResult(true);
        FullOcrModel model = LocalFullModels.ChineseV5;
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
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var mat = Cv2.ImDecode(imageBytes, ImreadModes.Color);
        var result = _paddleOcrEngine!.Run(mat);
        sw.Stop();
        _lastElapsedMs = sw.ElapsedMilliseconds;
        _lastOcrResult = result; var recognizedTexts = new List<string>();

        if (result.Regions != null && result.Regions.Any())
        {
          var filteredRegions = result.Regions.Where(r => r.Score >= _confidenceThreshold).ToList();
          if (!filteredRegions.Any())
          {
            recognizedTexts.Add("未识别到满足置信度要求的文本");
            return recognizedTexts;
          }          // 使用改进的排序算法：先按行分组，再按列排序
          var sortedRegions = SortRegionsByRowsAndColumns(filteredRegions);

          // 将排序后的文本合并
          var allText = string.Join(" ", sortedRegions.Select(r => r.Text));
          if (!string.IsNullOrWhiteSpace(allText))
          {
            recognizedTexts.Add(allText);
          }
          else
          {
            recognizedTexts.Add("识别到的文本为空");
          }
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
      debugInfo.AppendLine(); if (filteredRegions.Any())
      {
        // 显示排序前的原始顺序
        debugInfo.AppendLine("=== 原始识别顺序 ===");
        for (int regionIndex = 0; regionIndex < filteredRegions.Count; regionIndex++)
        {
          var region = filteredRegions[regionIndex];
          var bounds = GetActualBounds(region);
          debugInfo.AppendLine($"区域 {regionIndex + 1}: \"{region.Text}\" (X中心: {(bounds.Left + bounds.Right) / 2.0:F1}, Y中心: {(bounds.Top + bounds.Bottom) / 2.0:F1})");
        }
        debugInfo.AppendLine();

        // 显示排序后的顺序
        var sortedRegions = SortRegionsByRowsAndColumns(filteredRegions);
        debugInfo.AppendLine("=== 排序后的阅读顺序 ===");
        for (int sortedIndex = 0; sortedIndex < sortedRegions.Count; sortedIndex++)
        {
          var region = sortedRegions[sortedIndex];
          var bounds = GetActualBounds(region);
          debugInfo.AppendLine($"排序 {sortedIndex + 1}: \"{region.Text}\" (X中心: {(bounds.Left + bounds.Right) / 2.0:F1}, Y中心: {(bounds.Top + bounds.Bottom) / 2.0:F1})");
        }
        debugInfo.AppendLine();

        // 显示详细的区域信息
        debugInfo.AppendLine("=== 详细区域信息 ===");
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
      debugInfo.AppendLine($"本次识别耗时: {_lastElapsedMs} ms");
      debugInfo.AppendLine("=== 调试信息结束 ===");
      return debugInfo.ToString();
    }

    private static async Task<byte[]> ConvertSoftwareBitmapToBytesAsync(SoftwareBitmap bitmap)
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
    public void Dispose()
    {
      _paddleOcrEngine?.Dispose();
      _paddleOcrEngine = null;
      _regionBoundsCache.Clear();
      _isInitialized = false;
    }

    private List<PaddleOcrResultRegion> SortRegionsByRowsAndColumns(List<PaddleOcrResultRegion> regions)
    {
      if (!regions.Any()) return regions;

      // 计算所有区域的平均高度，用于判断是否在同一行
      var avgHeight = regions.Select(r => GetActualBounds(r).ActualHeight).Average();
      // 行间距阈值：使用平均高度的一半作为判断同一行的阈值
      var rowThreshold = avgHeight * 0.5;

      var sortedRegions = new List<PaddleOcrResultRegion>();
      var processedRegions = new HashSet<PaddleOcrResultRegion>();

      // 按Y坐标对所有区域进行初步排序
      var regionsByY = regions.OrderBy(r =>
      {
        var bounds = GetActualBounds(r);
        return (bounds.Top + bounds.Bottom) / 2.0; // Y中心
      }).ToList();

      foreach (var currentRegion in regionsByY)
      {
        if (processedRegions.Contains(currentRegion)) continue;

        // 找到与当前区域在同一行的所有区域
        var currentBounds = GetActualBounds(currentRegion);
        var currentYCenter = (currentBounds.Top + currentBounds.Bottom) / 2.0;

        var sameRowRegions = regions.Where(r =>
        {
          if (processedRegions.Contains(r)) return false;

          var bounds = GetActualBounds(r);
          var yCenter = (bounds.Top + bounds.Bottom) / 2.0;

          // 判断是否在同一行：Y中心差距小于阈值
          return Math.Abs(yCenter - currentYCenter) <= rowThreshold;
        }).ToList();

        // 对同一行的区域按X坐标从左到右排序
        var sortedRowRegions = sameRowRegions.OrderBy(r =>
        {
          var bounds = GetActualBounds(r);
          return (bounds.Left + bounds.Right) / 2.0; // X中心
        }).ToList();

        // 添加到结果列表
        sortedRegions.AddRange(sortedRowRegions);

        // 标记已处理
        foreach (var region in sameRowRegions)
        {
          processedRegions.Add(region);
        }
      }

      return sortedRegions;
    }
  }
}
