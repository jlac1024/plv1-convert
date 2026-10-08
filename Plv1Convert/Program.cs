using System.Diagnostics;
using System.Text;

namespace Plv1Convert;

static class Program
{
    const string Usage = """
        plv1-convert - convert Pelco DX-series (PLV1) AVI exports to MP4

        usage: plv1-convert <input.avi> [options]

          -o, --output <file>   output path (default: <input>_h264.mp4)
              --crf <n>         H.264 quality, lower is better (default 18)
              --fps <n>         override the detected frame rate
              --timestamp       show the recording clock (burned in when ffmpeg
                                is available, otherwise written as a .ass file
                                next to the video)
              --deblock         smooth the MPEG-4 block edges; alters the
                                picture, so avoid it for evidentiary copies
              --es-only         write just the raw .m4v elementary stream
              --keep-es         keep the .m4v alongside the MP4
              --ffmpeg <path>   use this ffmpeg instead of searching
          -h, --help            this text

        Without ffmpeg the video is written as MPEG-4 Part 2 rather than H.264:
        playable in VLC and most modern players, but not re-encoded.
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

        string? input = null, output = null, ffmpegPath = null;
        int crf = 18;
        double? fpsOverride = null;
        bool timestamp = false, deblock = false, esOnly = false, keepEs = false;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next(string name) =>
                i + 1 < args.Length ? args[++i] : throw new Exception($"{name} needs a value");

            switch (a)
            {
                case "-o" or "--output": output = Next(a); break;
                case "--crf": crf = int.Parse(Next(a)); break;
                case "--fps": fpsOverride = double.Parse(Next(a)); break;
                case "--ffmpeg": ffmpegPath = Next(a); break;
                case "--timestamp": timestamp = true; break;
                case "--deblock": deblock = true; break;
                case "--es-only": esOnly = true; break;
                case "--keep-es": keepEs = true; break;
                case "-h" or "--help": Console.WriteLine(Usage); return 0;
                default:
                    if (a.StartsWith('-')) { throw new Exception($"unknown option {a}"); }
                    if (input is not null) { throw new Exception("more than one input given"); }
                    input = a;
                    break;
            }
        }

        if (input is null) { throw new Exception("no input file given"); }
        if (!File.Exists(input)) { throw new Exception($"no such file: {input}"); }

        output ??= Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".",
            Path.GetFileNameWithoutExtension(input) + "_h264.mp4");
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        var video = PelcoAvi.Read(input);
        if (video.Frames.Count == 0) { throw new Exception("no video frames found - is this a Pelco PLV1 export?"); }
        if (!video.Frames.Exists(f => f.IsKey)) { throw new Exception("no keyframes found - is this a Pelco PLV1 export?"); }

        if (fpsOverride is double requested)
        {
            if (requested is <= 0 or > 60) { throw new Exception("--fps must be between 0 and 60"); }
            video = new PelcoVideo
            {
                Width = video.Width,
                Height = video.Height,
                TimeScale = 10000,
                FrameDuration = Math.Max(1, (int)Math.Round(10000 / requested)),
                Frames = video.Frames,
            };
        }

        var frames = PelcoAvi.DropOrphans(video.Frames, out int dropped);
        Report(frames, dropped, video);

        string es = Path.ChangeExtension(output, ".m4v");
        if (esOnly)
        {
            File.WriteAllBytes(es, Mpeg4.BuildStream(frames, video.Width, video.Height));
            Console.WriteLine($"wrote {es}");
            return 0;
        }

        string? subs = null;
        if (timestamp)
        {
            subs = Path.ChangeExtension(output, ".ass");
            File.WriteAllText(subs, Subtitles.Build(frames, video), new UTF8Encoding(false));
        }

        string? ffmpeg = ffmpegPath ?? FindFfmpeg();
        if (ffmpeg is null)
        {
            Console.WriteLine("ffmpeg not found - writing MPEG-4 Part 2 without re-encoding.");
            if (deblock) { Console.WriteLine("  note: --deblock needs ffmpeg, ignoring it"); }
            Mp4Muxer.Write(output, frames, video);
            if (subs is not null)
            {
                Console.WriteLine($"  clock written to {Path.GetFileName(subs)} " +
                                  "(load it as a subtitle track)");
            }
            if (keepEs) { File.WriteAllBytes(es, Mpeg4.BuildStream(frames, video.Width, video.Height)); }
        }
        else
        {
            File.WriteAllBytes(es, Mpeg4.BuildStream(frames, video.Width, video.Height));
            try
            {
                Encode(ffmpeg, es, output, crf, deblock, subs, video);
            }
            finally
            {
                if (!keepEs && File.Exists(es)) { File.Delete(es); }
                if (subs is not null && File.Exists(subs)) { File.Delete(subs); }
            }
        }

        Console.WriteLine($"wrote {output}");
        return 0;
    }

    static void Report(List<PelcoFrame> frames, int dropped, PelcoVideo video)
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(frames[0].Timestamp).ToLocalTime();
        var end = DateTimeOffset.FromUnixTimeSeconds(frames[^1].Timestamp).ToLocalTime();
        Console.WriteLine($"{frames.Count} frames  {video.Width}x{video.Height}  " +
                          $"{start:yyyy-MM-dd HH:mm:ss} -> {end:yyyy-MM-dd HH:mm:ss}  " +
                          $"({frames.Count / video.Fps:F1}s of footage at {video.Fps:0.##} fps)");
        if (dropped > 0)
        {
            Console.WriteLine($"  dropped {dropped} frame(s) with no valid reference " +
                              "(start of file / after gaps)");
        }
        foreach (var (at, seconds) in PelcoAvi.Gaps(frames))
        {
            Console.WriteLine($"  gap: {seconds,4}s of nothing recorded after " +
                              $"{DateTimeOffset.FromUnixTimeSeconds(at).ToLocalTime():HH:mm:ss}");
        }
    }

    static string? FindFfmpeg()
    {
        string exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

        string beside = Path.Combine(AppContext.BaseDirectory, exe);
        if (File.Exists(beside)) { return beside; }

        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string p = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(p)) { return p; }
            }
            catch (ArgumentException) { /* malformed PATH entry */ }
        }
        return null;
    }

    static void Encode(string ffmpeg, string es, string output, int crf, bool deblock,
                       string? subs, PelcoVideo video)
    {
        var filters = new List<string>();
        if (deblock) { filters.Add("pp7=qp=5:mode=medium"); }
        if (subs is not null) { filters.Add("subtitles=" + Path.GetFileName(subs)); }

        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(output)!,
        };
        foreach (string arg in new[]
                 {
                     "-hide_banner", "-v", "error",
                     "-r", $"{video.TimeScale}/{video.FrameDuration}", "-f", "m4v", "-i", es,
                     "-c:v", "libx264", "-preset", "slow", "-crf", crf.ToString(),
                     "-pix_fmt", "yuv420p", "-aspect", "4:3", "-movflags", "+faststart",
                     "-fps_mode", "passthrough",
                 })
        {
            psi.ArgumentList.Add(arg);
        }
        if (filters.Count > 0) { psi.ArgumentList.Add("-vf"); psi.ArgumentList.Add(string.Join(',', filters)); }
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add(output);

        using var p = Process.Start(psi) ?? throw new Exception("could not start ffmpeg");
        p.WaitForExit();
        if (p.ExitCode != 0) { throw new Exception($"ffmpeg failed with exit code {p.ExitCode}"); }
    }
}
