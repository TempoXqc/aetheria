using Aetheria.Server.Social;

namespace Aetheria.Tests;

public static class PartyTests
{
    [Test("Invite + accept forms a party with the inviter as leader.")]
    public static void InviteAccept_FormsParty()
    {
        var pm = new PartyManager();

        Assert.True(pm.Invite(1, 2, out _));
        Party? party = pm.Accept(2);

        Assert.True(party is not null);
        Assert.Equal(2, party!.Count);
        Assert.Equal(1, party.Leader);
        Assert.True(party.Contains(2));
        Assert.True(ReferenceEquals(pm.GetParty(1), pm.GetParty(2)));
    }

    [Test("Declining an invite leaves both players ungrouped.")]
    public static void Decline_LeavesUngrouped()
    {
        var pm = new PartyManager();
        pm.Invite(1, 2, out _);
        pm.Decline(2);

        Assert.True(pm.Accept(2) is null); // nothing pending anymore
        Assert.True(pm.GetParty(1) is null);
        Assert.True(pm.GetParty(2) is null);
    }

    [Test("A member already in a party cannot be invited.")]
    public static void Invite_RejectsGroupedTarget()
    {
        var pm = new PartyManager();
        pm.Invite(1, 2, out _);
        pm.Accept(2);

        Assert.False(pm.Invite(3, 2, out string error));
        Assert.True(error.Length > 0);
    }

    [Test("Only the leader can invite once a party exists.")]
    public static void Invite_OnlyLeader()
    {
        var pm = new PartyManager();
        pm.Invite(1, 2, out _);
        pm.Accept(2);

        Assert.False(pm.Invite(2, 3, out _)); // member 2 is not the leader
        Assert.True(pm.Invite(1, 3, out _));  // leader can
    }

    [Test("When the leader leaves, leadership passes; a party of one disbands.")]
    public static void Leave_PromotesThenDisbands()
    {
        var pm = new PartyManager();
        pm.Invite(1, 2, out _);
        pm.Accept(2);
        pm.Invite(1, 3, out _);
        pm.Accept(3);

        pm.Leave(1); // leader leaves a 3-party → 2 remain, new leader promoted
        Party? remaining = pm.GetParty(2);
        Assert.True(remaining is not null);
        Assert.Equal(2, remaining!.Leader);
        Assert.Equal(2, remaining.Count);

        pm.Leave(2); // now only one would remain → disband entirely
        Assert.True(pm.GetParty(2) is null);
        Assert.True(pm.GetParty(3) is null);
    }

    [Test("Reconnection: ReplaceMember hands the SAME seat to the new key — leadership follows.")]
    public static void ReplaceMember_KeepsSeatAndLeadership()
    {
        var pm = new PartyManager();
        pm.Invite(1, 2, out _);
        pm.Accept(2);

        Party? party = pm.ReplaceMember(1, 99); // the LEADER reconnects under a new key
        Assert.True(party is not null);
        Assert.Equal(99, party!.Leader);
        Assert.True(party.Contains(99));
        Assert.False(party.Contains(1));
        Assert.True(ReferenceEquals(pm.GetParty(99), party));
        Assert.True(pm.GetParty(1) is null);
        Assert.Equal(2, party.Count); // nobody gained or lost a seat
    }

    [Test("The party size cap is enforced.")]
    public static void Cap_IsEnforced()
    {
        var pm = new PartyManager(maxSize: 2);
        pm.Invite(1, 2, out _);
        pm.Accept(2);

        Assert.False(pm.Invite(1, 3, out string error)); // full at 2
        Assert.True(error.Length > 0);
    }
}
