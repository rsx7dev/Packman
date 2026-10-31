namespace Packman.Models;

/// <summary>Graph's supersedenceType values: how the new app treats the one it supersedes.</summary>
public static class SupersedenceType
{
    public const string Update = "update";
    public const string Replace = "replace";
}

/// <summary>One superseded app, written to Intune as a mobileAppSupersedence relationship.</summary>
public sealed record SupersedenceLink(string AppId, string Type);
