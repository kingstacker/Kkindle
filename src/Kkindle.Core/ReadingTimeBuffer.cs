namespace Kkindle.Core;

public sealed record ReadingTimeSlice(DateTimeOffset EndedAt, long Seconds);

// The timer may accumulate active seconds across hours of inactivity before a
// flush. Keep the original dates and offset rather than dating that whole
// batch from the time it is finally written to SQLite. Access on the UI thread.
public sealed class ReadingTimeBuffer
{
    private readonly Dictionary<(DateOnly Utc, DateOnly Local, TimeSpan Offset), ReadingTimeSlice> _slices = [];
    public long PendingSeconds => _slices.Values.Sum(s => s.Seconds);

    public void AddSecond(DateTimeOffset now) => Restore([new(now, 1)]);
    public void Clear() => _slices.Clear();
    public IReadOnlyList<ReadingTimeSlice> Drain()
    {
        var result = _slices.Values.OrderBy(s => s.EndedAt).ToArray();
        Clear();
        return result;
    }

    public void Restore(IEnumerable<ReadingTimeSlice> slices)
    {
        foreach (var slice in slices)
        {
            var creditedSecond = slice.EndedAt.AddSeconds(-1);
            var key = (DateOnly.FromDateTime(creditedSecond.UtcDateTime), DateOnly.FromDateTime(creditedSecond.DateTime), slice.EndedAt.Offset);
            if (_slices.TryGetValue(key, out var previous))
                _slices[key] = new(previous.EndedAt > slice.EndedAt ? previous.EndedAt : slice.EndedAt, checked(previous.Seconds + slice.Seconds));
            else _slices[key] = slice;
        }
    }
}
