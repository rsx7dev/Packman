namespace Packman.Helpers;

// The portable harness cannot restore System.Management.Automation offline (its transitive
// packages are not vendored), so it links this stand-in for Packman/Helpers/PowerShellLiteral.cs.
// SingleQuoted doubles every single-quote character PowerShell accepts as a delimiter, which is
// what CodeGeneration.EscapeSingleQuotedStringContent does.
public static class PowerShellLiteral
{
    public static string SingleQuoted(string? value) => string.Concat((value ?? "").Select(c =>
        c is '\'' or '‘' or '’' or '‚' or '‛' ? new string(c, 2) : c.ToString()));

    public static string DoubleQuoted(string? value) => (value ?? "")
        .Replace("`", "``")
        .Replace("\"", "`\"")
        .Replace("“", "`“")
        .Replace("”", "`”")
        .Replace("„", "`„")
        .Replace("$", "`$");
}
