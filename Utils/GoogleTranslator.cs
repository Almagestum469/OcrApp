using System;
using System.Collections.Generic;
using System.Net; // Required for WebUtility
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json; // Added for JSON parsing

// Note: This code relies on Nikse.SubtitleEdit.Core.Common.SeJsonParser.
// You need to ensure this class is available in your project.
// If it\'s not, the ConvertJsonObjectToStringLines method will need to be adapted
// to use a different JSON parsing mechanism (e.g., System.Text.Json or Newtonsoft.Json).

namespace OcrApp.Utils
{
  public static class GoogleTranslator
  {
    private static readonly HttpClient HttpClientInstance;

    static GoogleTranslator()
    {
      // Simplified HttpClient initialization.
      // If you need proxy support similar to HttpClientFactoryWithProxy,
      // you'll need to configure HttpClientInstance accordingly or use your existing factory.
      HttpClientInstance = new HttpClient();
      HttpClientInstance.DefaultRequestHeaders.Add("user-agent", "Mozilla/5.0 (Windows NT 6.1) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/41.0.2228.0 Safari/537.36");
      HttpClientInstance.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type", "application/json; charset=UTF-8");
      HttpClientInstance.BaseAddress = new Uri("https://translate.googleapis.com/");
    }

    /// <summary>
    /// Translates English text to Chinese (Simplified).
    /// </summary>
    /// <param name="textToTranslate">The English text to translate.</param>
    /// <returns>The translated Chinese text, or an error message if translation fails.</returns>
    public static async Task<string> TranslateEnglishToChineseAsync(string textToTranslate)
    {
      if (string.IsNullOrWhiteSpace(textToTranslate))
      {
        return string.Empty;
      }

      const string sourceLanguageCode = "en";
      const string targetLanguageCode = "zh-CN"; // For Simplified Chinese

      string jsonResultString;

      try
      {
        var text = textToTranslate.Trim();
        var encodedText = WebUtility.UrlEncode(text); // Using System.Net.WebUtility
        var url = $"translate_a/single?client=gtx&sl={sourceLanguageCode}&tl={targetLanguageCode}&dt=t&q={encodedText}";

        var response = await HttpClientInstance.GetAsync(url);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        jsonResultString = Encoding.UTF8.GetString(bytes).Trim();

        if (!response.IsSuccessStatusCode)
        {
          Console.Error.WriteLine($"Google Translate API Error: {response.StatusCode} - {jsonResultString}");
          return $"Error (HTTP {response.StatusCode}): Could not translate text.";
        }
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine($"Google Translate request failed: {ex.Message}");
        return $"Error: Translation request failed ({ex.Message}).";
      }

      // This part assumes Nikse.SubtitleEdit.Core.Common.SeJsonParser is available.
      List<string> resultList = ConvertJsonObjectToStringLines(jsonResultString);
      return string.Join(string.Empty, resultList); // Join directly, as API usually returns segments of a single translation
    }

    /// <summary>
    /// Parses the JSON response from Google Translate.
    /// This method is based on the structure of the original Nikse.SubtitleEdit code
    /// and assumes the availability of Nikse.SubtitleEdit.Core.Common.SeJsonParser.
    /// </summary>
    /// <param name="jsonResult">The JSON string returned by the Google Translate API.</param>
    /// <returns>A list of translated string segments.</returns>
    private static List<string> ConvertJsonObjectToStringLines(string jsonResult)
    {
      var lines = new List<string>();
      try
      {
        using (JsonDocument doc = JsonDocument.Parse(jsonResult))
        {
          JsonElement root = doc.RootElement;
          if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
          {
            JsonElement firstOuterArrayElement = root[0];
            if (firstOuterArrayElement.ValueKind == JsonValueKind.Array)
            {
              foreach (JsonElement innerArrayElement in firstOuterArrayElement.EnumerateArray())
              {
                if (innerArrayElement.ValueKind == JsonValueKind.Array && innerArrayElement.GetArrayLength() > 0)
                {
                  JsonElement translationElement = innerArrayElement[0];
                  if (translationElement.ValueKind == JsonValueKind.String)
                  {
                    lines.Add(translationElement.GetString() ?? string.Empty);
                  }
                }
              }
            }
          }
        }
      }
      catch (JsonException ex)
      {
        Console.Error.WriteLine($"Error parsing Google Translate JSON response: {ex.Message}");
        // Optionally, return an error indicator or throw
        lines.Add($"Error parsing translation: {ex.Message}");
      }
      return lines;
    }
  }
}
