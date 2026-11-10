namespace Packman.Models;

/// <summary>An Entra (Azure AD) security group returned by a directory search.</summary>
public class EntraGroup
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

/// <summary>Which kind of directory object the members panel searches for and lists.</summary>
public enum MemberKind { Users, Devices }

/// <summary>A member of an Entra group (device, user or nested group).</summary>
public class GroupMember
{
    public const string User = "User";
    public const string Device = "Device";
    public const string Group = "Group";

    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Kind { get; set; } = "";   // Device | User | Group

    /// <summary>What tells two members with the same name apart: a user's sign-in name, a device's operating system.</summary>
    public string Detail { get; set; } = "";
    public bool HasDetail => Detail.Length > 0;

    /// <summary>The Graph filter that finds users whose name, sign-in name, email or surname starts with <paramref name="literal"/> (already OData-escaped).</summary>
    public static string UserFilter(string literal) =>
        $"startswith(displayName,'{literal}') or startswith(userPrincipalName,'{literal}') or startswith(mail,'{literal}') or startswith(surname,'{literal}')";

    /// <summary>The Graph filter that finds devices whose name starts with <paramref name="literal"/> (already OData-escaped).</summary>
    public static string DeviceFilter(string literal) => $"startswith(displayName,'{literal}')";

    /// <summary>The kind a group mostly holds, so its panel opens on the search that fits it. An empty group opens on users.</summary>
    public static MemberKind DefaultKind(IEnumerable<GroupMember> members)
    {
        int users = 0, devices = 0;
        foreach (var member in members)
        {
            if (member.Kind == User) users++;
            else if (member.Kind == Device) devices++;
        }
        return devices > users ? MemberKind.Devices : MemberKind.Users;
    }

    /// <summary>True when the group holds both users and devices, which makes it hard to tell why an app reached a device.</summary>
    public static bool IsMixed(IEnumerable<GroupMember> members)
    {
        bool users = false, devices = false;
        foreach (var member in members)
        {
            users |= member.Kind == User;
            devices |= member.Kind == Device;
        }
        return users && devices;
    }

    /// <summary>"3 users · 12 devices · 1 group"; kinds with no member are left out, and an empty group reads "No members".</summary>
    public static string Summary(IReadOnlyCollection<GroupMember> members)
    {
        static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

        int users = members.Count(m => m.Kind == User);
        int devices = members.Count(m => m.Kind == Device);
        int groups = members.Count(m => m.Kind == Group);
        int other = members.Count - users - devices - groups;
        var parts = new List<string>();
        if (users > 0) parts.Add(Count(users, "user"));
        if (devices > 0) parts.Add(Count(devices, "device"));
        if (groups > 0) parts.Add(Count(groups, "group"));
        if (other > 0) parts.Add($"{other} other");
        return parts.Count == 0 ? "No members" : string.Join(" · ", parts);
    }

    /// <summary>The members shown for <paramref name="kind"/>: that kind first, then nested groups and anything else, which belong to neither list.</summary>
    public static List<GroupMember> Visible(IEnumerable<GroupMember> members, MemberKind kind)
    {
        var hidden = kind == MemberKind.Users ? Device : User;
        var shown = kind == MemberKind.Users ? User : Device;
        return members.Where(m => m.Kind != hidden)
            .OrderBy(m => m.Kind == shown ? 0 : 1)
            .ThenBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
