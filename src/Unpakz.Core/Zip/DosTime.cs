using System;

namespace Unpakz.Core.Zip;

/// <summary>MS-DOS date/time (ZIP's native timestamp): local time, 2-second resolution, 1980-2107.</summary>
public static class DosTime
{
    /// <summary>Converts a packed (date &lt;&lt; 16 | time) value. Returns false for unset (0) or invalid calendar/clock values.</summary>
    public static bool TryConvert(uint dos, out DateTime local)
    {
        local = default;
        var date = (int)(dos >> 16);
        var time = (int)(dos & 0xFFFF);
        if (date == 0) return false;

        var year = 1980 + (date >> 9);
        var month = (date >> 5) & 0xF;
        var day = date & 0x1F;
        var hour = time >> 11;
        var minute = (time >> 5) & 0x3F;
        var second = (time & 0x1F) * 2;

        if (month is < 1 or > 12 || day < 1 || hour > 23 || minute > 59 || second > 59) return false;
        if (day > DateTime.DaysInMonth(year, month)) return false;

        local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
        return true;
    }
}
