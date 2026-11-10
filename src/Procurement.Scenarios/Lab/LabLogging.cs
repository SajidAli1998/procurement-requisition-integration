using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Procurement.Scenarios.Lab;

/// <summary>Collects every log line a lab host writes, so scenarios can check that secrets never reach the logs.</summary>
public sealed class LabLogSink : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new SinkLogger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class SinkLogger(LabLogSink sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            sink.Lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)}{(exception is null ? "" : " " + exception)}");
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
                foreach (var (k, v) in values) sink.Lines.Enqueue($"  {k}={v}");
        }
    }
}
