using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using System;
using System.Text;

namespace OcrApp.Utils
{
  public static class GoogleTranslator
  {
    private static readonly HttpClient HttpClientInstance;
    private static readonly string API_KEY; static GoogleTranslator()
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

      try
      {
        var url = $"https://translate-pa.googleapis.com/v1/translateHtml?key={API_KEY}";

        // 构建请求体
        var requestBody = JsonSerializer.Serialize(new object[]
        {
          new object[]
          {
            new[] { textToTranslate.Trim() },
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
        if (root.ValueKind == JsonValueKind.Array &&
            root.GetArrayLength() > 0 &&
            root[0].ValueKind == JsonValueKind.Array &&
            root[0].GetArrayLength() > 0)
        {
          return root[0][0].GetString() ?? string.Empty;
        }

        return string.Empty;
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine($"Google Translate request failed: {ex.Message}");
        return $"Error: Translation request failed ({ex.Message}).";
      }
    }
  }
}
