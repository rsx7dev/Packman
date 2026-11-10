using Packman.Models;
using Xunit;

namespace Packman.Tests;

/// <summary>What the group members panel decides from a member list, and the filters its two searches send.</summary>
public sealed class GroupMemberTests
{
    private static GroupMember User(string name) => new() { Id = name, DisplayName = name, Kind = GroupMember.User };
    private static GroupMember Device(string name) => new() { Id = name, DisplayName = name, Kind = GroupMember.Device };
    private static GroupMember Group(string name) => new() { Id = name, DisplayName = name, Kind = GroupMember.Group };

    [Fact]
    public void AUserIsFoundByNameSignInNameEmailOrSurname()
    {
        var filter = GroupMember.UserFilter("o''neil");

        foreach (var property in new[] { "displayName", "userPrincipalName", "mail", "surname" })
            Assert.Contains($"startswith({property},'o''neil')", filter);
    }

    [Fact]
    public void ADeviceIsFoundByNameOnly() =>
        Assert.Equal("startswith(displayName,'LAP')", GroupMember.DeviceFilter("LAP"));

    [Fact]
    public void ThePanelOpensOnWhatTheGroupMostlyHolds()
    {
        Assert.Equal(MemberKind.Users, GroupMember.DefaultKind([]));
        Assert.Equal(MemberKind.Users, GroupMember.DefaultKind([User("a"), Group("g")]));
        Assert.Equal(MemberKind.Devices, GroupMember.DefaultKind([Device("d")]));
        Assert.Equal(MemberKind.Devices, GroupMember.DefaultKind([User("a"), Device("d"), Device("e")]));
        Assert.Equal(MemberKind.Users, GroupMember.DefaultKind([User("a"), Device("d")]));
    }

    [Fact]
    public void OnlyAGroupWithBothUsersAndDevicesIsMixed()
    {
        Assert.False(GroupMember.IsMixed([]));
        Assert.False(GroupMember.IsMixed([User("a"), Group("g")]));
        Assert.False(GroupMember.IsMixed([Device("d"), Group("g")]));
        Assert.True(GroupMember.IsMixed([User("a"), Device("d")]));
    }

    [Fact]
    public void TheSummaryCountsEachKindAndLeavesOutTheEmptyOnes()
    {
        Assert.Equal("No members", GroupMember.Summary([]));
        Assert.Equal("1 user", GroupMember.Summary([User("a")]));
        Assert.Equal("2 users · 1 device · 1 group", GroupMember.Summary([User("a"), User("b"), Device("d"), Group("g")]));
        Assert.Equal("1 device · 1 other", GroupMember.Summary([Device("d"), new GroupMember { Kind = "servicePrincipal" }]));
    }

    [Fact]
    public void TheListShowsTheChosenKindFirstThenNestedGroupsAndNeverTheOtherKind()
    {
        GroupMember[] members = [Group("Pilot"), Device("PC-2"), User("zoë"), User("Adele"), Device("PC-1")];

        Assert.Equal(["Adele", "zoë", "Pilot"], GroupMember.Visible(members, MemberKind.Users).Select(m => m.DisplayName));
        Assert.Equal(["PC-1", "PC-2", "Pilot"], GroupMember.Visible(members, MemberKind.Devices).Select(m => m.DisplayName));
    }

    [Fact]
    public void AMemberWithoutADetailShowsNoSecondLine()
    {
        Assert.False(User("a").HasDetail);
        Assert.True(new GroupMember { Detail = "adele@contoso.com" }.HasDetail);
    }
}
