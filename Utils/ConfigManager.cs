using System;
using System.IO;
using System.Text.Json;

namespace OcrApp.Utils
{
  public static class ConfigManager
  {
    private static readonly Lazy<Configuration> _config = new(() => LoadConfiguration());

    public static Configuration Config => _config.Value;

    private static Configuration LoadConfiguration()
    {
      try
      {
        string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

        if (!File.Exists(configPath))
        {
          throw new FileNotFoundException($"Configuration file not found at: {configPath}");
        }

        string json = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<Configuration>(json, new JsonSerializerOptions
        {
          PropertyNameCaseInsensitive = true
        });

        return config ?? throw new InvalidOperationException("Failed to deserialize configuration");
      }
      catch (Exception ex)
      {
        throw new InvalidOperationException($"Failed to load configuration: {ex.Message}", ex);
      }
    }
  }

  public class Configuration
  {
    public GoogleTranslateConfig GoogleTranslate { get; set; } = new();
  }

  public class GoogleTranslateConfig
  {
    public string ApiKey { get; set; } = string.Empty;
  }
}