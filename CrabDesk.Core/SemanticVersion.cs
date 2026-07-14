namespace CrabDesk.Core;

public sealed class SemanticVersion : IComparable<SemanticVersion>
{
    private SemanticVersion(int major, int minor, int patch, string[] prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public IReadOnlyList<string> Prerelease { get; }
    public bool IsPrerelease => Prerelease.Count > 0;

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }
        var buildSeparator = normalized.IndexOf('+');
        if (buildSeparator >= 0)
        {
            normalized = normalized[..buildSeparator];
        }
        var prereleaseSeparator = normalized.IndexOf('-');
        var main = prereleaseSeparator < 0 ? normalized : normalized[..prereleaseSeparator];
        var prerelease = prereleaseSeparator < 0
            ? []
            : normalized[(prereleaseSeparator + 1)..].Split('.');
        var parts = main.Split('.');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var major) || major < 0 ||
            !int.TryParse(parts[1], out var minor) || minor < 0 ||
            !int.TryParse(parts[2], out var patch) || patch < 0 ||
            prerelease.Any(identifier => string.IsNullOrEmpty(identifier) ||
                identifier.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, prerelease);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }
        var main = Major.CompareTo(other.Major);
        if (main == 0) main = Minor.CompareTo(other.Minor);
        if (main == 0) main = Patch.CompareTo(other.Patch);
        if (main != 0)
        {
            return main;
        }
        if (!IsPrerelease && !other.IsPrerelease)
        {
            return 0;
        }
        if (!IsPrerelease)
        {
            return 1;
        }
        if (!other.IsPrerelease)
        {
            return -1;
        }

        var count = Math.Max(Prerelease.Count, other.Prerelease.Count);
        for (var index = 0; index < count; index++)
        {
            if (index >= Prerelease.Count) return -1;
            if (index >= other.Prerelease.Count) return 1;
            var left = Prerelease[index];
            var right = other.Prerelease[index];
            var leftNumeric = int.TryParse(left, out var leftNumber);
            var rightNumeric = int.TryParse(right, out var rightNumber);
            int comparison;
            if (leftNumeric && rightNumeric)
            {
                comparison = leftNumber.CompareTo(rightNumber);
            }
            else if (leftNumeric)
            {
                comparison = -1;
            }
            else if (rightNumeric)
            {
                comparison = 1;
            }
            else
            {
                comparison = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
            }
            if (comparison != 0)
            {
                return comparison;
            }
        }
        return 0;
    }

    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}" + (IsPrerelease ? "-" + string.Join('.', Prerelease) : string.Empty);
}
