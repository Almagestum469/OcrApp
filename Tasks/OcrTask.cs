using System;
using System.Collections.Generic;
using Windows.Graphics.Imaging;

namespace OcrApp.Tasks
{
  internal sealed class OcrTask
  {
    public OcrTask(SoftwareBitmap image)
    {
      Id = Guid.NewGuid();
      CreatedAt = DateTimeOffset.UtcNow;
      Image = image;
    }

    public Guid Id { get; }
    public DateTimeOffset CreatedAt { get; }
    public SoftwareBitmap? Image { get; set; }
    public IReadOnlyList<string>? OcrTexts { get; set; }
    public IReadOnlyList<string>? Translations { get; set; }
    public string? Error { get; set; }

    public OcrTaskStage Stage
    {
      get
      {
        if (!string.IsNullOrEmpty(Error)) return OcrTaskStage.Error;
        if (Translations != null) return OcrTaskStage.Translated;
        if (OcrTexts != null) return OcrTaskStage.OcrCompleted;
        if (Image != null) return OcrTaskStage.Captured;
        return OcrTaskStage.Pending;
      }
    }

    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool IsCaptured => Image != null;
    public bool IsOcrDone => OcrTexts != null;
    public bool IsTranslated => Translations != null;

    public void ReleaseImage()
    {
      Image?.Dispose();
      Image = null;
    }
  }

  internal enum OcrTaskStage
  {
    Pending,
    Captured,
    OcrCompleted,
    Translated,
    Error
  }
}
