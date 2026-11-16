using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services;

/// <summary>
/// DACL of the pipes the Tray opens to the Agent (<c>Homebase.NotificationPipe</c>,
/// <c>Homebase.MobileBridge</c>; Device Hub R-04): the server's own identity, SYSTEM,
/// Administrators and the configured Windows user (<c>mobile:userSid</c>), else the INTERACTIVE
/// group. Never Authenticated Users: any other local account (or a process running as it) could
/// then inject frames the LocalSystem Agent acts on.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class TrayPipeSecurity
{
    /// <summary>What a client open needs: read/write data and attributes, plus SYNCHRONIZE.</summary>
    private const PipeAccessRights ClientRights = PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;

    public static PipeSecurity Build(string? userSid, ILogger logger)
    {
        var security = new PipeSecurity();
        void Allow(SecurityIdentifier sid) =>
            security.AddAccessRule(new PipeAccessRule(sid, ClientRights, AccessControlType.Allow));

        // The server's own identity needs CreateNewInstance to open the 2nd..Nth instance; without
        // it the pipe works for exactly one client and then every new instance is access-denied.
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (identity.User is { } self)
                security.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));
        }
        Allow(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        Allow(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        Allow(ParseUserSid(userSid, logger) ?? new SecurityIdentifier(WellKnownSidType.InteractiveSid, null));
        return security;
    }

    /// <summary>The configured user's SID, or <c>null</c> when it is unset or not a valid SID.</summary>
    public static SecurityIdentifier? ParseUserSid(string? userSid, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(userSid)) return null;
        try
        {
            return new SecurityIdentifier(userSid.Trim());
        }
        catch (ArgumentException)
        {
            logger.LogWarning("mobile:userSid '{Sid}' is not a valid SID; using INTERACTIVE.", userSid);
            return null;
        }
    }
}
