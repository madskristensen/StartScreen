using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

namespace StartScreen.Helpers
{
    internal static class BackgroundBatchProcessor
    {
        internal static async Task ProcessAsync<TSource, TResult>(IReadOnlyList<TSource> items,
            Func<TSource, TResult> read, Func<IReadOnlyList<TResult>, CancellationToken, Task> publish,
            CancellationToken cancellationToken)
        {
            var next = -1;
            async Task ProcessWorkerAsync()
            {
                var batch = new List<TResult>(8);
                var first = true;
                while (true)
                {
                    await TaskScheduler.Default;
                    cancellationToken.ThrowIfCancellationRequested();
                    var index = Interlocked.Increment(ref next);
                    if (index >= items.Count)
                        break;

                    try
                    {
                        batch.Add(read(items[index]));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        await ex.LogAsync();
                    }

                    if (batch.Count > 0 && (first || batch.Count == 8))
                    {
                        await publish(batch, cancellationToken);
                        batch.Clear();
                        first = false;
                    }
                }

                if (batch.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await publish(batch, cancellationToken);
                }
            }

            await Task.WhenAll(Task.Run(ProcessWorkerAsync, cancellationToken), Task.Run(ProcessWorkerAsync, cancellationToken));
        }
    }
}