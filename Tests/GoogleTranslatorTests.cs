using Microsoft.VisualStudio.TestTools.UnitTesting;
using OcrApp.Utils; // Assuming GoogleTranslator is in this namespace
using System.Threading.Tasks;
using System;

namespace OcrApp.Tests
{
  [TestClass]
  public class GoogleTranslatorTests
  {
    [TestMethod]
    public async Task TranslateEnglishToChineseAsync_HelloWorld_ReturnsTranslation()
    {
      // Arrange
      string inputText = "Hello world!";
      string expectedErrorPrefix = "Error";

      // Act
      string result = await GoogleTranslator.TranslateEnglishToChineseAsync(inputText);

      // Assert
      Assert.IsNotNull(result, "翻译结果不应为 null。");
      Assert.IsFalse(string.IsNullOrEmpty(result), "翻译结果不应为空字符串。");
      Assert.IsFalse(result.StartsWith(expectedErrorPrefix), $"翻译不应返回错误信息。实际结果: {result}");

      // 额外打印，方便查看结果
      Console.WriteLine($"输入: {inputText}");
      Console.WriteLine($"翻译结果: {result}");

      // 可以添加一个更具体的断言，例如检查结果是否包含中文字符，
      // 但这取决于API返回的具体格式，并且可能会使测试变得脆弱。
      // 例如，可以检查结果是否与输入不同：
      Assert.AreNotEqual(inputText, result, "翻译结果应与输入文本不同。");
    }
  }
}
