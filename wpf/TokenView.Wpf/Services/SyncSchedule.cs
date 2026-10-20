namespace TokenView.Wpf.Services;

/// <summary>定时采集判定；与 core::scheduler 一致。</summary>
public sealed class SyncSchedule
{
    private int _interval;
    private TimeSpan? _next;

    public bool Due(TimeSpan elapsed, int intervalMinutes, bool paused)
    {
        if (paused || intervalMinutes == 0)
        {
            _interval = 0;
            _next = null;
            return false;
        }
        var period = TimeSpan.FromMinutes(intervalMinutes);
        if (_interval != intervalMinutes || _next is null)
        {
            _interval = intervalMinutes;
            _next = elapsed + period;
            return false;
        }
        if (_next is { } next && elapsed >= next)
        {
            _next = elapsed + period;
            return true;
        }
        return false;
    }
}
