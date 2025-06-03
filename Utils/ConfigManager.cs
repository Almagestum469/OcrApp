using System;
using System.IO;
using System.Reflection;
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
        // 首先尝试从外部文件加载(开发环境方便调试)
        string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
        string json;

        if (File.Exists(configPath))
        {
          // 如果外部文件存在，优先使用外部文件
          json = File.ReadAllText(configPath);
        }
        else
        {
          // 如果外部文件不存在，从嵌入资源加载
          var assembly = Assembly.GetExecutingAssembly();
          var resourceName = "OcrApp.appsettings.json";

          using var stream = assembly.GetManifestResourceStream(resourceName);
          if (stream == null)
          {
            throw new FileNotFoundException($"嵌入式资源未找到: {resourceName}");
          }

          using var reader = new StreamReader(stream);
          json = reader.ReadToEnd();
        }

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