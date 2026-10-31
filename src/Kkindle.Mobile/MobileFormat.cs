namespace Kkindle.Mobile;

internal static class MobileFormat
{
    public static string Size(long bytes)
    {
        if (bytes <= 0) return "—";
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    public static string ReadingTime(long seconds)
    {
        if (seconds < 60) return $"{seconds} 秒";
        if (seconds < 3600) return $"{seconds / 60d:0.#} 分";
        return $"{seconds / 3600d:0.#} 小时";
    }
}
