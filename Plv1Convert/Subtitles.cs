using System.Text;

namespace Plv1Convert;

/// <summary>Builds an ASS subtitle track showing the recorder's wall clock.</summary>
static class Subtitles
{
    static string Time(double s)
    {
        int h = (int)(s / 3600), m = (int)(s / 60) % 60;
        double sec = s % 60;
        return $"{h}:{m:00}:{sec:00.00}";
    }

    public static string Build(List<PelcoFrame> frames)
    {
        var sb = new StringBuilder();
        sb.Append("[Script Info]\n")
          .Append("ScriptType: v4.00+\n")
          .Append($"PlayResX: {Mpeg4.Width}\n")
          .Append($"PlayResY: {Mpeg4.Height}\n\n")
          .Append("[V4+ Styles]\n")
          .Append("Format: Name, Fontname, Fontsize, PrimaryColour, OutlineColour, ")
          .Append("BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, ")
          .Append("Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, ")
          .Append("MarginL, MarginR, MarginV, Encoding\n")
          .Append("Style: ts,Arial,14,&H00FFFFFF,&H00000000,&H80000000,1,0,0,0,")
          .Append("100,100,0,0,1,1,1,1,6,6,4,1\n\n")
          .Append("[Events]\n")
          .Append("Format: Layer, Start, End, Style, Name, MarginL, MarginR, ")
          .Append("MarginV, Effect, Text\n");

        // One event per distinct recording second, each running until the next
        // begins. A recorded second does not always hold exactly Fps frames, so
        // fixed one-second events would leave gaps and the clock would flicker.
        var marks = new List<(int Index, uint Ts)>();
        uint? prev = null;
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i].Timestamp != prev)
            {
                marks.Add((i, frames[i].Timestamp));
                prev = frames[i].Timestamp;
            }
        }

        for (int n = 0; n < marks.Count; n++)
        {
            double start = marks[n].Index / (double)Mpeg4.Fps;
            double end = (n + 1 < marks.Count ? marks[n + 1].Index : frames.Count)
                         / (double)Mpeg4.Fps;
            var stamp = DateTimeOffset.FromUnixTimeSeconds(marks[n].Ts).ToLocalTime();
            sb.Append($"Dialogue: 0,{Time(start)},{Time(end)},ts,,0,0,0,,")
              .Append($"{stamp:yyyy-MM-dd HH:mm:ss}\n");
        }
        return sb.ToString();
    }
}
