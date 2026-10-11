namespace LumiLink.Desktop.Transport;

// 5 falhas de auth por IP em 1 min → bloqueia 5 min.
public sealed class RateLimiter
{
    private readonly Dictionary<string, List<DateTime>> _fails = new();
    private readonly Dictionary<string, DateTime> _blocked = new();
    private readonly object _lock = new();
    private readonly int _maxFails;
    private readonly TimeSpan _window;
    private readonly TimeSpan _blockFor;

    public RateLimiter(int maxFails = 5, TimeSpan? window = null, TimeSpan? blockFor = null)
    {
        _maxFails = maxFails;
        _window = window ?? TimeSpan.FromMinutes(1);
        _blockFor = blockFor ?? TimeSpan.FromMinutes(5);
    }

    public bool IsBlocked(string ip)
    {
        lock (_lock)
        {
            if (_blocked.TryGetValue(ip, out var until))
            {
                if (DateTime.UtcNow < until) return true;
                _blocked.Remove(ip);
            }
            return false;
        }
    }

    public void Fail(string ip)
    {
        lock (_lock)
        {
            if (!_fails.TryGetValue(ip, out var list)) _fails[ip] = list = new();
            var cutoff = DateTime.UtcNow - _window;
            list.RemoveAll(t => t < cutoff);
            list.Add(DateTime.UtcNow);
            if (list.Count >= _maxFails)
            {
                _blocked[ip] = DateTime.UtcNow + _blockFor;
                list.Clear();
            }
        }
    }

    public void Success(string ip)
    {
        lock (_lock) { _fails.Remove(ip); _blocked.Remove(ip); }
    }
}
