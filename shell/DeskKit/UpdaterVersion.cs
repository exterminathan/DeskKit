// SemVer 2.0 parse and precedence for updates (contract 16.4). Build metadata is ignored.
using System.Text.RegularExpressions;

namespace DeskKit.Shell;

public sealed partial class SemVer : IComparable<SemVer>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public IReadOnlyList<string> Pre { get; }
    public bool IsPrerelease => Pre.Count > 0;
    public string Text { get; }

    private SemVer(int major, int minor, int patch, IReadOnlyList<string> pre, string text)
    {
        Major = major; Minor = minor; Patch = patch; Pre = pre; Text = text;
    }

    [GeneratedRegex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$")]
    private static partial Regex Pattern();

    public static SemVer? TryParse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = Pattern().Match(s.Trim());
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups[1].Value, out var a) || !int.TryParse(m.Groups[2].Value, out var b)
            || !int.TryParse(m.Groups[3].Value, out var c)) return null;
        var pre = m.Groups[4].Success ? m.Groups[4].Value.Split('.') : Array.Empty<string>();
        // SemVer: numeric pre-release identifiers must not have leading zeros.
        if (pre.Any(p => p.Length > 1 && p[0] == '0' && p.All(char.IsAsciiDigit))) return null;
        return new SemVer(a, b, c, pre, s.Trim());
    }

    public int CompareTo(SemVer? o)
    {
        if (o is null) return 1;
        int r = Major.CompareTo(o.Major);
        if (r == 0) r = Minor.CompareTo(o.Minor);
        if (r == 0) r = Patch.CompareTo(o.Patch);
        if (r != 0) return r;
        if (Pre.Count == 0 && o.Pre.Count == 0) return 0;
        if (Pre.Count == 0) return 1;      // 0.2.0 > 0.2.0-beta.1
        if (o.Pre.Count == 0) return -1;
        for (int i = 0; i < Math.Min(Pre.Count, o.Pre.Count); i++)
        {
            var x = Pre[i]; var y = o.Pre[i];
            bool xn = x.All(char.IsAsciiDigit), yn = y.All(char.IsAsciiDigit);
            int c;
            if (xn && yn) c = x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y);
            else if (xn) c = -1;
            else if (yn) c = 1;
            else c = string.CompareOrdinal(x, y);
            if (c != 0) return c < 0 ? -1 : 1;
        }
        return Pre.Count.CompareTo(o.Pre.Count);
    }

    /// <summary>-1, 0 or 1; an unparsable version sorts below every valid one.</summary>
    public static int Compare(string? a, string? b)
    {
        var x = TryParse(a); var y = TryParse(b);
        if (x == null && y == null) return 0;
        if (x == null) return -1;
        if (y == null) return 1;
        return Math.Sign(x.CompareTo(y));
    }

    public override string ToString() => Text;
}
