namespace Packman.Helpers;

/// <summary>Builds the command-line argument text the Add buttons fill in.</summary>
public static class ArgumentLine
{
    private static readonly char[] KeyEnd = ['=', ':', '"'];

    /// <summary>
    /// Adds <paramref name="token"/> unless the line already has it. A token that carries a
    /// value (<c>/D=</c>, <c>ALLUSERS=1</c>, <c>/v"..."</c>) counts as present when the line has
    /// the same name, whatever its value. Tokens land before the piece that starts with
    /// <paramref name="lastToken"/>, for switches such as NSIS's /D= that must come last.
    /// </summary>
    public static string Append(string line, string token, string? lastToken = null)
    {
        line = line.Trim();
        var pieces = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var key = Key(token);
        var wholeToken = token.IndexOfAny(KeyEnd) < 0;

        var present = pieces.Any(p => wholeToken
                ? p.Equals(key, StringComparison.OrdinalIgnoreCase)
                : p.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            // Tokens with spaces in them ("/log C:\x.log") never match a single piece.
            || $" {line} ".Contains($" {token} ", StringComparison.OrdinalIgnoreCase);
        if (present) return line;
        if (line.Length == 0) return token;

        if (lastToken != null && !token.Equals(lastToken, StringComparison.OrdinalIgnoreCase))
        {
            var lastKey = Key(lastToken);
            var at = Array.FindIndex(pieces, p => p.StartsWith(lastKey, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) return string.Join(' ', pieces[..at].Append(token).Concat(pieces[at..]));
        }
        return $"{line} {token}";
    }

    /// <summary><c>NAME=value</c>, with the value quoted when it is empty or has whitespace or quotes.</summary>
    public static string MsiProperty(string name, string value)
    {
        var quote = value.Length == 0 || value.Any(c => char.IsWhiteSpace(c) || c == '"');
        return quote ? $"{name}=\"{value.Replace("\"", "\"\"")}\"" : $"{name}={value}";
    }

    // Everything up to and including the first '=', ':' or '"'; a bare switch is its own key.
    private static string Key(string token)
    {
        var at = token.IndexOfAny(KeyEnd);
        return at < 0 ? token : token[..(at + 1)];
    }
}
