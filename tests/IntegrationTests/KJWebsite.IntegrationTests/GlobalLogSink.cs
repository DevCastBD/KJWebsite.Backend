using Microsoft.Extensions.Logging;

namespace KJWebsite.IntegrationTests;

public sealed record LeakRecord(string Category, string Label, string Value);

/// <summary>Checks logs as they are written. Registration never scans earlier lines.</summary>
public sealed class LogSink
{
    private readonly object _gate = new();
    private readonly HashSet<(string Label, string Value)> _seeds = [];
    private readonly List<(LeakRecord Record, bool Reported)> _violations = [];
    public ILoggerProvider Provider { get; }
    public LogSink() => Provider = new SinkProvider(this);
    public IReadOnlyList<LeakRecord> Violations { get { lock (_gate) return _violations.Select(v => v.Record).ToArray(); } }
    public void Register(string label, string value)
    {
        if (value is null || value.Length < 12) throw new ArgumentException("Seeds must contain at least 12 characters.", nameof(value));
        lock (_gate) _seeds.Add((label, value));
    }
    public void AssertNone()
    {
        lock (_gate)
        {
            var pending = _violations.Where(v => !v.Reported).Select(v => v.Record).ToArray();
            for (var i = 0; i < _violations.Count; i++) _violations[i] = (_violations[i].Record, true);
            if (pending.Length != 0) throw new InvalidOperationException(string.Join(Environment.NewLine,
                pending.Select(v => $"logger '{v.Category}' logged '{v.Value}' ({v.Label})")));
        }
    }
    private void Check(string category, string line)
    {
        lock (_gate)
            foreach (var seed in _seeds)
                if (line.Contains(seed.Value, StringComparison.OrdinalIgnoreCase))
                    _violations.Add((new(category, seed.Label, seed.Value), false));
    }
    private sealed class SinkProvider(LogSink owner) : ILoggerProvider
    {
        // Own scopes let parallel hosts share this provider without replacing each other's scope provider.
        private readonly IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
        public ILogger CreateLogger(string categoryName) => new Logger(owner, categoryName, _scopes);
        public void Dispose() { }
    }
    private sealed class Logger(LogSink owner, string category, IExternalScopeProvider scopes) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => scopes.Push(state);
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = new System.Text.StringBuilder().Append(category).Append(' ').Append(logLevel).Append(' ').Append(formatter(state, exception)).Append(' ').Append(exception);
            scopes.ForEachScope((scope, text) => text.Append(' ').Append(scope), line);
            owner.Check(category, line.ToString());
        }
    }
}

public static class GlobalLogSink
{
    public static LogSink Default { get; } = new();
}
