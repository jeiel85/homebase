using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using LocalOpsBot.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LocalOpsBot.Tests.Agent.Services;

public sealed class TrayPipeSecurityTests
{
    private static List<PipeAccessRule> Rules(PipeSecurity security) =>
        security.GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow)
            .ToList();

    private static List<SecurityIdentifier> Allowed(PipeSecurity security) =>
        Rules(security).Select(r => (SecurityIdentifier)r.IdentityReference).ToList();

    private static SecurityIdentifier Sid(WellKnownSidType type) => new(type, null);

    [Fact]
    public void Without_a_configured_user_the_interactive_group_is_admitted_and_never_authenticated_users()
    {
        var sids = Allowed(TrayPipeSecurity.Build(null, NullLogger.Instance));

        Assert.Contains(Sid(WellKnownSidType.LocalSystemSid), sids);
        Assert.Contains(Sid(WellKnownSidType.BuiltinAdministratorsSid), sids);
        Assert.Contains(Sid(WellKnownSidType.InteractiveSid), sids);
        Assert.DoesNotContain(Sid(WellKnownSidType.AuthenticatedUserSid), sids);
        Assert.DoesNotContain(Sid(WellKnownSidType.WorldSid), sids);
    }

    [Fact]
    public void A_configured_user_replaces_the_interactive_group()
    {
        const string user = "S-1-5-21-1111111111-2222222222-3333333333-1001";

        var sids = Allowed(TrayPipeSecurity.Build(user, NullLogger.Instance));

        Assert.Contains(new SecurityIdentifier(user), sids);
        Assert.DoesNotContain(Sid(WellKnownSidType.InteractiveSid), sids);
    }

    [Fact]
    public void An_invalid_configured_sid_falls_back_to_the_interactive_group()
    {
        var sids = Allowed(TrayPipeSecurity.Build("not-a-sid", NullLogger.Instance));

        Assert.Contains(Sid(WellKnownSidType.InteractiveSid), sids);
    }

    [Fact]
    public void The_server_identity_may_open_further_pipe_instances()
    {
        using var me = WindowsIdentity.GetCurrent();

        var rules = Rules(TrayPipeSecurity.Build(null, NullLogger.Instance));

        Assert.Contains(rules, r => r.IdentityReference.Equals(me.User)
                                    && r.PipeAccessRights.HasFlag(PipeAccessRights.CreateNewInstance));
    }
}
