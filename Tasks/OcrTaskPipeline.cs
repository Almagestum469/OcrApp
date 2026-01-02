using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OcrApp.Engines;
using OcrApp.Utils;

namespace OcrApp.Tasks
{
  internal sealed partial class OcrTaskPipeline : IDisposable
  {
    private readonly ConcurrentQueue<OcrTask> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<IOcrEngine?> _ocrEngineProvider;
    private readonly Task _worker;

    public OcrTaskPipeline(
        Func<IOcrEngine?> ocrEngineProvider)
    {
      _ocrEngineProvider = ocrEngineProvider;
      _worker = Task.Run(() => RunAsync(_cts.Token));
    }

    public event Action<OcrTask>? TaskUpdated;
    public event Action<Exception>? PipelineError;

    public void Enqueue(OcrTask task)
    {
      _queue.Enqueue(task);
      _signal.Release();
      TaskUpdated?.Invoke(task);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
      try
      {
        while (!cancellationToken.IsCancellationRequested)
        {
          await _signal.WaitAsync(cancellationToken);

          if (!_queue.TryDequeue(out var task))
          {
            continue;
          }

          await ProcessTaskAsync(task, cancellationToken);
        }
      }
      catch (OperationCanceledException)
      {
        // normal shutdown
      }
      catch (Exception ex)
      {
        PipelineError?.Invoke(ex);
      }
    }

    private async Task ProcessTaskAsync(OcrTask task, CancellationToken cancellationToken)
    {
      try
      {
        var engine = _ocrEngineProvider();
        if (engine == null)
        {
          task.Error = "OCR引擎未就绪";
          TaskUpdated?.Invoke(task);
          return;
        }

        if (task.Image == null)
        {
          task.Error = "任务缺少图像";
          TaskUpdated?.Invoke(task);
          return;
        }

        var ocrResults = await engine.RecognizeAsync(task.Image);
        cancellationToken.ThrowIfCancellationRequested();

        task.OcrTexts = ocrResults;
        task.ReleaseImage();
        TaskUpdated?.Invoke(task);

        if (ocrResults == null || ocrResults.Count == 0)
        {
          task.Translations = Array.Empty<string>();
          TaskUpdated?.Invoke(task);
          return;
        }

        var translations = await TranslateTextsAsync(ocrResults, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        task.Translations = translations;
        TaskUpdated?.Invoke(task);
      }
      catch (OperationCanceledException)
      {
        // ignore
      }
      catch (Exception ex)
      {
        task.Error = ex.Message;
        task.ReleaseImage();
        TaskUpdated?.Invoke(task);
      }
    }

    private static async Task<IReadOnlyList<string>> TranslateTextsAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
      var translations = new List<string>();

      foreach (var text in texts)
      {
        if (cancellationToken.IsCancellationRequested)
        {
          break;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
          continue;
        }

        var translated = await GoogleTranslator.TranslateEnglishToChineseAsync(text);
        translations.Add(translated);
      }

      return translations;
    }

    public void Dispose()
    {
      _cts.Cancel();
      _signal.Release();
      try
      {
        _worker.Wait(500);
      }
      catch
      {
        // ignore
      }
      _cts.Dispose();
      _signal.Dispose();
    }
  }
}
