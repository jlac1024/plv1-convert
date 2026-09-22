namespace Plv1Convert;

static class Program
{
    const int Fps = 5;              // recorder writes 5 frames per second

    const string Usage = """
        plv1-convert - read Pelco DX-series (PLV1) AVI exports

        usage: plv1-convert <input.avi>

          -h, --help            this text
        """;

    static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception e)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 1;
        }
    }

    static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        string? input = null;
        foreach (string a in args)
        {
            if (a.StartsWith('-')) throw new Exception($"unknown option {a}");
            if (input is not null) throw new Exception("more than one input given");
            input = a;
        }

        if (input is null) throw new Exception("no input file given");
        if (!File.Exists(input)) throw new Exception($"no such file: {input}");

        var all = PelcoAvi.ReadFrames(input);
        if (all.Count == 0) throw new Exception("no video frames found - is this a Pelco PLV1 export?");
        if (!all.Exists(f => f.IsKey)) throw new Exception("no keyframes found - is this a Pelco PLV1 export?");

        var frames = PelcoAvi.DropOrphans(all, out int dropped);
        Report(frames, dropped);
        return 0;
    }

    static void Report(List<PelcoFrame> frames, int dropped)
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(frames[0].Timestamp).ToLocalTime();
        var end = DateTimeOffset.FromUnixTimeSeconds(frames[^1].Timestamp).ToLocalTime();
        Console.WriteLine($"{frames.Count} frames  {start:yyyy-MM-dd HH:mm:ss} -> " +
                          $"{end:yyyy-MM-dd HH:mm:ss}  " +
                          $"({frames.Count / (double)Fps:F1}s of footage at {Fps} fps)");
        if (dropped > 0)
            Console.WriteLine($"  dropped {dropped} frame(s) with no valid reference " +
                              "(start of file / after gaps)");
        foreach (var (at, seconds) in PelcoAvi.Gaps(frames))
            Console.WriteLine($"  gap: {seconds,4}s of nothing recorded after " +
                              $"{DateTimeOffset.FromUnixTimeSeconds(at).ToLocalTime():HH:mm:ss}");
    }
}
