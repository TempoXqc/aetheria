namespace Aetheria.Server.Social;

/// <summary>A group of players. Members are opaque int keys (the server uses peer ids).</summary>
public sealed class Party
{
    private readonly List<int> _members = new();

    public Party(int id, int leader)
    {
        Id = id;
        Leader = leader;
        _members.Add(leader);
    }

    public int Id { get; }
    public int Leader { get; private set; }
    public IReadOnlyList<int> Members => _members;
    public int Count => _members.Count;

    internal void Add(int member) => _members.Add(member);

    internal bool Remove(int member)
    {
        _members.Remove(member);
        if (Leader == member && _members.Count > 0)
        {
            Leader = _members[0]; // promote the longest-standing member
        }

        return _members.Count <= 1; // signal "should disband"
    }

    public bool Contains(int member) => _members.Contains(member);
}

/// <summary>
/// Party membership rules, kept pure (no networking, no entities) so they are trivially testable.
/// Members are int keys. Flow: a leader-to-be invites a target; the target has at most one pending
/// invite (newest wins); accepting joins the inviter's party (created on demand); leaving promotes a
/// new leader or disbands the party when only one member would remain.
/// </summary>
public sealed class PartyManager
{
    private readonly int _maxSize;
    private readonly Dictionary<int, Party> _partyByMember = new();
    private readonly Dictionary<int, int> _pendingInviteByTarget = new(); // target -> inviter
    private int _nextPartyId = 1;

    public PartyManager(int maxSize = 40) => _maxSize = maxSize;

    public Party? GetParty(int member) => _partyByMember.TryGetValue(member, out Party? p) ? p : null;

    /// <summary>Record an invite. Fails if the target is already in a party or the inviter's party is full.</summary>
    public bool Invite(int inviter, int target, out string error)
    {
        error = string.Empty;

        if (inviter == target)
        {
            error = "You cannot invite yourself.";
            return false;
        }

        if (_partyByMember.ContainsKey(target))
        {
            error = "That player is already in a party.";
            return false;
        }

        Party? party = GetParty(inviter);
        if (party is not null && party.Count >= _maxSize)
        {
            error = "The party is full.";
            return false;
        }

        if (party is not null && party.Leader != inviter)
        {
            error = "Only the party leader can invite.";
            return false;
        }

        _pendingInviteByTarget[target] = inviter;
        return true;
    }

    /// <summary>Accept the pending invite, joining (or creating) the inviter's party.</summary>
    public Party? Accept(int target)
    {
        if (!_pendingInviteByTarget.Remove(target, out int inviter))
        {
            return null;
        }

        if (_partyByMember.ContainsKey(target))
        {
            return null; // joined another party in the meantime
        }

        Party? party = GetParty(inviter);
        if (party is null)
        {
            party = new Party(_nextPartyId++, inviter);
            _partyByMember[inviter] = party;
        }

        if (party.Count >= _maxSize)
        {
            return null;
        }

        party.Add(target);
        _partyByMember[target] = party;
        return party;
    }

    public void Decline(int target) => _pendingInviteByTarget.Remove(target);

    /// <summary>
    /// Leave the current party. Promotes a new leader if needed; a party reduced to one member is
    /// disbanded. Returns the party the member left, or null if they were not in one.
    /// </summary>
    public Party? Leave(int member)
    {
        if (!_partyByMember.Remove(member, out Party? party))
        {
            return null;
        }

        bool disband = party.Remove(member);
        if (disband)
        {
            foreach (int remaining in party.Members.ToArray())
            {
                _partyByMember.Remove(remaining);
                party.Remove(remaining);
            }
        }

        return party;
    }

    public bool HasPendingInvite(int target) => _pendingInviteByTarget.ContainsKey(target);
}
