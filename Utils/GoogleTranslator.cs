using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using System;
using System.Text;
using System.Collections.Generic;
using System.Collections.Concurrent;

namespace OcrApp.Utils
{
  public static class GoogleTranslator
  {
    private static readonly HttpClient HttpClientInstance;
    private static readonly string API_KEY;

    // 缓存字典，键为英文文本，值为中文翻译结果
    private static readonly ConcurrentDictionary<string, string> TranslationCache = new ConcurrentDictionary<string, string>();

    // 缓存队列，用于跟踪最近使用的条目（最旧的在队列头部）
    private static readonly LinkedList<string> CacheQueue = new LinkedList<string>();

    // 最大缓存条目数
    private const int MaxCacheSize = 5000;

    // 用于线程安全地更新缓存队列
    private static readonly object CacheLock = new object();

    static GoogleTranslator()
    {
      try
      {
        API_KEY = ConfigManager.Config.GoogleTranslate.ApiKey;

        if (string.IsNullOrWhiteSpace(API_KEY))
        {
          throw new InvalidOperationException("Google Translate API key is not configured. Please check your appsettings.json file.");
        }

        HttpClientInstance = new HttpClient();
        HttpClientInstance.DefaultRequestHeaders.Add("user-agent", "Mozilla/5.0 (Windows NT 6.1) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/41.0.2228.0 Safari/537.36");
        HttpClientInstance.DefaultRequestHeaders.Add("accept", "*/*");
        HttpClientInstance.DefaultRequestHeaders.Add("accept-language", "zh-CN,zh;q=0.9,en;q=0.8");
        HttpClientInstance.DefaultRequestHeaders.Add("x-client-data", "CIy2yQEIorbJAQipncoBCNn0ygEIlKHLAQiSo8sBCIWgzQEIu+rOAQiQ8s4BCKPyzgEY4OLOAQ==");
      }
      catch (Exception ex)
      {
        throw new InvalidOperationException($"Failed to initialize GoogleTranslator: {ex.Message}", ex);
      }
    }

    /// <summary>
    /// Translates English text to Chinese (Simplified).
    /// </summary>
    /// <param name="textToTranslate">The English text to translate.</param>
    /// <returns>The translated Chinese text, or an error message if translation fails.</returns>
    public static async Task<string> TranslateEnglishToChineseAsync(string textToTranslate)
    {
      if (string.IsNullOrWhiteSpace(textToTranslate))
        return string.Empty;

      string trimmedText = textToTranslate.Trim();

      // 检查缓存中是否存在
      if (TranslationCache.TryGetValue(trimmedText, out string? cachedTranslation) && cachedTranslation != null)
      {
        // 更新缓存队列（将此条目移到最近使用）
        lock (CacheLock)
        {
          // 从队列中删除此条目（如果存在）
          var node = CacheQueue.Find(trimmedText);
          if (node != null)
          {
            CacheQueue.Remove(node);
          }

          // 将条目添加到队列尾部（最近使用）
          CacheQueue.AddLast(trimmedText);
        }

        // 返回缓存的翻译结果
        return cachedTranslation;
      }

      try
      {
        var url = $"https://translate-pa.googleapis.com/v1/translateHtml?key={API_KEY}";

        // 构建请求体
        var requestBody = JsonSerializer.Serialize(new object[]
        {
          new object[]
          {
            new[] { trimmedText },
            "en",
            "zh-CN"
          },
          "te_lib"
        });

        var content = new StringContent(requestBody, Encoding.UTF8, "application/json+protobuf");

        var response = await HttpClientInstance.PostAsync(url, content);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
          Console.Error.WriteLine($"Google Translate API Error: {response.StatusCode} - {json}");
          return $"Error (HTTP {response.StatusCode}): Could not translate text.";
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // 新API的响应格式为 [["翻译结果"]]
        string translationResult = string.Empty;
        if (root.ValueKind == JsonValueKind.Array &&
            root.GetArrayLength() > 0 &&
            root[0].ValueKind == JsonValueKind.Array &&
            root[0].GetArrayLength() > 0)
        {
          translationResult = root[0][0].GetString() ?? string.Empty;

          // 只有成功获取翻译结果才添加到缓存
          if (!string.IsNullOrEmpty(translationResult))
          {
            // 添加到缓存
            AddToCache(trimmedText, translationResult);
          }
        }

        return translationResult;
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine($"Google Translate request failed: {ex.Message}");
        return $"Error: Translation request failed ({ex.Message}).";
      }
    }

    /// <summary>
    /// 将翻译结果添加到缓存中
    /// </summary>
    /// <param name="sourceText">源文本</param>
    /// <param name="translatedText">翻译后的文本</param>
    private static void AddToCache(string sourceText, string translatedText)
    {
      lock (CacheLock)
      {
        // 如果此条目已存在于缓存中，先从队列中移除
        if (TranslationCache.ContainsKey(sourceText))
        {
          var node = CacheQueue.Find(sourceText);
          if (node != null)
          {
            CacheQueue.Remove(node);
          }
        }
        // 如果缓存已满，移除最旧的条目
        else if (TranslationCache.Count >= MaxCacheSize && CacheQueue.Count > 0)
        {
          var first = CacheQueue.First;
          if (first != null)
          {
            var oldestKey = first.Value;
            CacheQueue.RemoveFirst();
            TranslationCache.TryRemove(oldestKey, out _);
          }
        }

        // 添加新条目到缓存
        TranslationCache[sourceText] = translatedText;
        CacheQueue.AddLast(sourceText);
      }
    }

    /// <summary>
    /// 清空翻译缓存
    /// </summary>
    public static void ClearCache()
    {
      lock (CacheLock)
      {
        TranslationCache.Clear();
        CacheQueue.Clear();
      }
    }
  }
}
