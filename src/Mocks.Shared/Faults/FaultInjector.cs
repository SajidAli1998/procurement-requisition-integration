namespace Mocks.Shared.Faults;

public sealed class FaultInjector
{
    private readonly Lock _gate = new();
    private readonly List<ScriptedFault> _script = [];
    private readonly Dictionary<FaultKind, int> _injected = [];
    private FaultSettings _settings;
    private Random _random;

    public FaultInjector(FaultSettings? initial = null)
    {
        _settings = initial ?? new FaultSettings();
        _random = new Random(_settings.Seed);
    }

    public FaultSettings Settings
    {
        get { lock (_gate) return _settings; }
    }

    public void Configure(FaultSettings settings)
    {
        lock (_gate)
        {
            _settings = settings;
            _random = new Random(settings.Seed);
        }
    }

    public void Enqueue(ScriptedFault fault)
    {
        lock (_gate) _script.Add(fault);
    }

    public void Reset()
    {
        lock (_gate)
        {
            _settings = new FaultSettings();
            _random = new Random(_settings.Seed);
            _script.Clear();
            _injected.Clear();
        }
    }

    public object Snapshot()
    {
        lock (_gate)
        {
            return new
            {
                settings = _settings,
                pendingScript = _script.ToArray(),
                injected = _injected.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            };
        }
    }

    public FaultDecision Decide(string method, string path)
    {
        lock (_gate)
        {
            var decision = DecideScripted(method, path) ?? DecideRandom(method);
            if (decision.Kind != FaultKind.None)
            {
                _injected[decision.Kind] = _injected.GetValueOrDefault(decision.Kind) + 1;
            }
            return decision;
        }
    }

    private FaultDecision? DecideScripted(string method, string path)
    {
        for (var i = 0; i < _script.Count; i++)
        {
            var fault = _script[i];
            if (fault.Method is not null && !string.Equals(fault.Method, method, StringComparison.OrdinalIgnoreCase)) continue;
            if (fault.PathContains is not null && !path.Contains(fault.PathContains, StringComparison.OrdinalIgnoreCase)) continue;

            if (fault.Skip > 0)
            {
                _script[i] = fault with { Skip = fault.Skip - 1 };
                return null;
            }

            if (fault.Count <= 1) _script.RemoveAt(i);
            else _script[i] = fault with { Count = fault.Count - 1 };

            return fault.Kind switch
            {
                FaultKind.Throttle => new FaultDecision(FaultKind.Throttle, 429, fault.RetryAfterSeconds),
                FaultKind.Error => new FaultDecision(FaultKind.Error, fault.StatusCode),
                FaultKind.LostResponse => new FaultDecision(FaultKind.LostResponse),
                FaultKind.Delay => new FaultDecision(FaultKind.Delay, DelayMs: fault.DelayMs),
                _ => FaultDecision.None,
            };
        }
        return null;
    }

    private FaultDecision DecideRandom(string method)
    {
        var s = _settings;
        if (s.ThrottleRate > 0 && _random.NextDouble() < s.ThrottleRate)
            return new FaultDecision(FaultKind.Throttle, 429, s.ThrottleRetryAfterSeconds);
        if (s.TransientErrorRate > 0 && _random.NextDouble() < s.TransientErrorRate)
            return new FaultDecision(FaultKind.Error, 503);
        var isWrite = method is "POST" or "PUT" or "PATCH" or "DELETE";
        if (isWrite && s.LostResponseRate > 0 && _random.NextDouble() < s.LostResponseRate)
            return new FaultDecision(FaultKind.LostResponse);
        if (s.LatencyMs > 0)
            return new FaultDecision(FaultKind.Delay, DelayMs: s.LatencyMs);
        return FaultDecision.None;
    }
}
