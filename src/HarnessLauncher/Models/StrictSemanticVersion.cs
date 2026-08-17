namespace HarnessLauncher.Models;

/// <summary>
/// Strict SemVer 2.0 parser/comparator used by both Runtime manifest and App
/// update version checks. Keeps prerelease identifiers and rejects malformed
/// suffixes instead of silently truncating them.
/// </summary>
public sealed class StrictSemanticVersion : IComparable<StrictSemanticVersion>, IEquatable<StrictSemanticVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public IReadOnlyList<string> Prerelease { get; }

    private StrictSemanticVersion(int major, int minor, int patch, IReadOnlyList<string> prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public static bool TryParse(string rawValue, out StrictSemanticVersion? version)
    {
        version = null;
        var remainder = rawValue.Trim().TrimStart('v', 'V');

        int? NextComponent()
        {
            var digits = new string(remainder.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0) return null;
            remainder = remainder[digits.Length..];
            return int.Parse(digits);
        }

        var major = NextComponent();
        if (major is null || !remainder.StartsWith('.')) return false;
        remainder = remainder[1..];
        var minor = NextComponent();
        if (minor is null || !remainder.StartsWith('.')) return false;
        remainder = remainder[1..];
        var patch = NextComponent();
        if (patch is null) return false;

        var prereleaseIdentifiers = new List<string>();
        if (remainder.StartsWith('-'))
        {
            remainder = remainder[1..];
            var prereleaseText = new string(remainder.TakeWhile(c => c != '+').ToArray());
            remainder = remainder[prereleaseText.Length..];
            var identifiers = prereleaseText.Split('.');
            if (identifiers.Length == 0) return false;
            foreach (var identifier in identifiers)
            {
                if (identifier.Length == 0 ||
                    !identifier.All(c => c < 128 && (char.IsDigit(c) || char.IsLetter(c) || c == '-')))
                {
                    return false;
                }
                var isAllDigits = identifier.All(char.IsDigit);
                if (char.IsDigit(identifier[0]) && !isAllDigits) return false;
                if (identifier[0] == '0' && identifier.Length > 1 && isAllDigits) return false;
                prereleaseIdentifiers.Add(identifier);
            }
        }

        if (remainder.StartsWith('+'))
        {
            remainder = remainder[1..];
            if (remainder.Length == 0 ||
                !remainder.All(c => c < 128 && (char.IsDigit(c) || char.IsLetter(c) || c == '-' || c == '.')))
            {
                return false;
            }
        }
        else if (remainder.Length != 0)
        {
            return false;
        }

        version = new StrictSemanticVersion(major.Value, minor.Value, patch.Value, prereleaseIdentifiers);
        return true;
    }

    public override string ToString()
    {
        var baseVersion = $"{Major}.{Minor}.{Patch}";
        return Prerelease.Count == 0 ? baseVersion : $"{baseVersion}-{string.Join('.', Prerelease)}";
    }

    public int CompareTo(StrictSemanticVersion? other)
    {
        if (other is null) return 1;
        if (Major != other.Major) return Major.CompareTo(other.Major);
        if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
        if (Patch != other.Patch) return Patch.CompareTo(other.Patch);
        // A version with a prerelease is lower than the same version without.
        if (Prerelease.Count == 0 && other.Prerelease.Count == 0) return 0;
        if (Prerelease.Count == 0) return 1;
        if (other.Prerelease.Count == 0) return -1;
        foreach (var (lhs, rhs) in Prerelease.Zip(other.Prerelease))
        {
            if (lhs == rhs) continue;
            var lhsNumeric = int.TryParse(lhs, out var l);
            var rhsNumeric = int.TryParse(rhs, out var r);
            if (lhsNumeric && rhsNumeric) return l.CompareTo(r);
            // Numeric identifiers always compare lower than alphanumeric.
            if (lhsNumeric) return -1;
            if (rhsNumeric) return 1;
            return string.CompareOrdinal(lhs, rhs);
        }
        return Prerelease.Count.CompareTo(other.Prerelease.Count);
    }

    public bool Equals(StrictSemanticVersion? other) => CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is StrictSemanticVersion other && Equals(other);
    public override int GetHashCode() => ToString().GetHashCode(StringComparison.Ordinal);
    public static bool operator <(StrictSemanticVersion lhs, StrictSemanticVersion rhs) => lhs.CompareTo(rhs) < 0;
    public static bool operator >(StrictSemanticVersion lhs, StrictSemanticVersion rhs) => lhs.CompareTo(rhs) > 0;
    public static bool operator <=(StrictSemanticVersion lhs, StrictSemanticVersion rhs) => lhs.CompareTo(rhs) <= 0;
    public static bool operator >=(StrictSemanticVersion lhs, StrictSemanticVersion rhs) => lhs.CompareTo(rhs) >= 0;
    public static bool operator ==(StrictSemanticVersion? lhs, StrictSemanticVersion? rhs) =>
        lhs is null ? rhs is null : lhs.Equals(rhs);
    public static bool operator !=(StrictSemanticVersion? lhs, StrictSemanticVersion? rhs) => !(lhs == rhs);
}
