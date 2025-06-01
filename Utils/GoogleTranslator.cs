using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using System;

namespace OcrApp.Utils
{
  public static class GoogleTranslator
  {
    private static readonly HttpClient HttpClientInstance;

    static GoogleTranslator()
    {
      HttpClientInstance = new HttpClient();
      HttpClientInstance.DefaultRequestHeaders.Add("user-agent", "Mozilla/5.0 (Windows NT 6.1) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/41.0.2228.0 Safari/537.36");
      HttpClientInstance.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type", "application/json; charset=UTF-8");
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
        var encodedText = WebUtility.UrlEncode(textToTranslate.Trim());
        var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl=zh-CN&dt=t&q={encodedText}";
        var response = await HttpClientInstance.GetAsync(url);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
          Console.Error.WriteLine($"Google Translate API Error: {response.StatusCode} - {json}");
          return $"Error (HTTP {response.StatusCode}): Could not translate text.";
        }
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
        {
          var arr = root[0];
          if (arr.ValueKind == JsonValueKind.Array)
          {
            string result = string.Empty;
            foreach (var seg in arr.EnumerateArray())
            {
              if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0)
                result += seg[0].GetString();
            }
            return result;
          }
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
