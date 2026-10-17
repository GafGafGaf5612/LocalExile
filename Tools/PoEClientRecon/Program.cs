using System.Text;

namespace PoEClientRecon;

/// <summary>
/// Fast string extraction and needle search over large binaries (ASCII + UTF-16LE),
/// used for protocol/endpoint reconnaissance of the Path of Exile client.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                """
                usage:
                  PoEClientRecon strings <file> [--min N] [--out out.txt]
                  PoEClientRecon find    <file> <needle> [--ignore-case]
                """);
            return 2;
        }

        try
        {
            return args[0] switch
            {
                "strings" => Strings(args[1], args),
                "find" => Find(args[1], args),
                _ => 2,
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int Strings(string path, string[] args)
    {
        int min = int.TryParse(GetOption(args, "--min"), out var m) ? m : 5;
        byte[] data = File.ReadAllBytes(path);

        var output = new List<string>();
        foreach (var s in ExtractAscii(data, min))
            output.Add($"{s.Offset:X8}\tascii\t{s.Value}");
        foreach (var s in ExtractUtf16(data, min))
            output.Add($"{s.Offset:X8}\tutf16\t{s.Value}");

        string? outPath = GetOption(args, "--out");
        if (outPath is not null)
        {
            File.WriteAllLines(outPath, output);
            Console.WriteLine($"{output.Count} strings -> {outPath}");
        }
        else
        {
            foreach (var line in output)
                Console.WriteLine(line);
        }
        return 0;
    }

    private static int Find(string path, string[] args)
    {
        string needle = args[2];
        bool ignoreCase = args.Contains("--ignore-case");
        byte[] data = File.ReadAllBytes(path);

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        int hits = 0;

        // ASCII / UTF-8 text match.
        string asciiHaystack = Encoding.Latin1.GetString(data);
        int idx = 0;
        while ((idx = asciiHaystack.IndexOf(needle, idx, comparison)) >= 0)
        {
            Console.WriteLine($"{idx:X8}\tascii\t{Context(data, idx, needle.Length)}");
            idx += needle.Length;
            hits++;
        }

        // UTF-16LE match.
        byte[] needle16 = Encoding.Unicode.GetBytes(needle);
        if (needle16.Length >= 2)
        {
            int i = 0;
            while ((i = IndexOf(data, needle16, i, ignoreCase)) >= 0)
            {
                Console.WriteLine($"{i:X8}\tutf16\t{ContextUtf16(data, i, needle.Length)}");
                i += 2;
                hits++;
            }
        }

        Console.WriteLine($"--- {hits} hit(s) for '{needle}' ---");
        return 0;
    }

    private static string Context(byte[] data, int offset, int len)
    {
        int start = Math.Max(0, offset - 32);
        int end = Math.Min(data.Length, offset + len + 32);
        return Sanitize(Encoding.Latin1.GetString(data, start, end - start));
    }

    private static string ContextUtf16(byte[] data, int offset, int chars)
    {
        int start = Math.Max(0, offset - 64);
        if ((start & 1) != 0) start--;
        int end = Math.Min(data.Length, offset + (chars * 2) + 64);
        if ((end & 1) != 0) end--;
        return Sanitize(Encoding.Unicode.GetString(data, start, end - start));
    }

    private static string Sanitize(string s) => new(s.Select(c => c is >= ' ' and < (char)127 ? c : '.').ToArray());

    private readonly record struct Extracted(int Offset, string Value);

    private static IEnumerable<Extracted> ExtractAscii(byte[] data, int min)
    {
        var sb = new StringBuilder();
        int start = 0;
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            if (b is >= 32 and < 127)
            {
                if (sb.Length == 0) start = i;
                sb.Append((char)b);
            }
            else
            {
                if (sb.Length >= min) yield return new Extracted(start, sb.ToString());
                sb.Clear();
            }
        }
        if (sb.Length >= min) yield return new Extracted(start, sb.ToString());
    }

    private static IEnumerable<Extracted> ExtractUtf16(byte[] data, int min)
    {
        var sb = new StringBuilder();
        int start = 0;
        for (int i = 0; i + 1 < data.Length; i += 2)
        {
            int c = data[i] | (data[i + 1] << 8);
            if (c is >= 32 and < 127)
            {
                if (sb.Length == 0) start = i;
                sb.Append((char)c);
            }
            else
            {
                if (sb.Length >= min) yield return new Extracted(start, sb.ToString());
                sb.Clear();
            }
        }
        if (sb.Length >= min) yield return new Extracted(start, sb.ToString());
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start, bool ignoreCase)
    {
        int limit = haystack.Length - needle.Length;
        for (int i = start; i <= limit; i += 2)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                byte a = haystack[i + j];
                byte b = needle[j];
                if (ignoreCase)
                {
                    a = ToLower(a);
                    b = ToLower(b);
                }
                if (a != b)
                {
                    match = false;
                    break;
                }
            }
            if (match) return i;
        }
        return -1;
    }

    private static byte ToLower(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;

    private static string? GetOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.Ordinal))
                return args[i + 1];
        return null;
    }
}
