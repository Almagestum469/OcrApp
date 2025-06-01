using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;

namespace OcrApp.Engines
{
  public interface IOcrEngine
  {
    Task<bool> InitializeAsync();
    Task<List<string>> RecognizeAsync(SoftwareBitmap bitmap);
    string GenerateDebugInfo();
  }
}
