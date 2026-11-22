using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace LocalOpsBot.Agent.Services;

/// <summary>
/// Closes <c>C:\ProgramData\Homebase</c> to standard users. It inherits ProgramData's ACL, which lets
/// every user create files and folders in it; but the service (SYSTEM) reads its config there and
/// creates <c>mobile\</c> there, so a user could plant <c>config\appsettings.json</c> before it exists
/// or a junction where <c>mobile\</c> will be. After this: SYSTEM and Administrators full control,
/// Users read — except <c>logs\</c>, where each user's Tray still creates its own log file (no folders,
/// so no junctions). <c>data\</c> and <c>mobile\</c> keep their own SYSTEM/Administrators-only ACLs.
/// Checked at every service start so installs from before this rule are closed by the next update; written
/// only when it differs, since a write propagates to everything under the folder.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProgramDataAcl
{
    public static readonly string Root =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Homebase");

    /// <summary>Applies the rule when running as SYSTEM; returns what it did, for the log.</summary>
    public static IReadOnlyList<string> Restrict(string root)
    {
        var notes = new List<string>();
        using (var identity = WindowsIdentity.GetCurrent())
            if (!identity.IsSystem) return notes; // a developer run would lock itself out

        if (!Directory.Exists(root)) return notes;
        if (IsLink(new DirectoryInfo(root)))
        {
            notes.Add($"{root} is a link, not a folder; left alone. Reinstall Homebase.");
            return notes;
        }
        var logs = Path.Combine(root, "logs");
        // A link a user put here would carry the ACL change below onto its target: remove the link only.
        RemoveLinks(root, notes);
        if (Directory.Exists(logs)) RemoveLinks(logs, notes);

        Apply(new DirectoryInfo(root), RootSecurity());
        if (Directory.Exists(logs)) Apply(new DirectoryInfo(logs), LogsSecurity());
        return notes;
    }

    private static void Apply(DirectoryInfo dir, DirectorySecurity wanted)
    {
        if (!AlreadyApplied(dir.GetAccessControl(), wanted)) dir.SetAccessControl(wanted);
    }

    /// <summary>
    /// True when <paramref name="actual"/> already has what writing <paramref name="wanted"/> would give it: the same
    /// inheritance protection and the same explicit rules (inherited ones follow from the parent).
    /// </summary>
    internal static bool AlreadyApplied(DirectorySecurity actual, DirectorySecurity wanted) =>
        actual.AreAccessRulesProtected == wanted.AreAccessRulesProtected
        && ExplicitRules(actual).SetEquals(ExplicitRules(wanted));

    private static HashSet<(string, FileSystemRights, InheritanceFlags, PropagationFlags, AccessControlType)> ExplicitRules(DirectorySecurity s) =>
        s.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(r => (r.IdentityReference.Value, r.FileSystemRights, r.InheritanceFlags, r.PropagationFlags, r.AccessControlType))
            .ToHashSet();

    internal static DirectorySecurity RootSecurity()
    {
        var s = new DirectorySecurity();
        s.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        s.AddAccessRule(Rule(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl, all));
        s.AddAccessRule(Rule(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl, all));
        s.AddAccessRule(Rule(WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute, all));
        return s;
    }

    /// <summary>On top of what logs\ inherits: users create files (not folders); a file's creator keeps it.</summary>
    internal static DirectorySecurity LogsSecurity()
    {
        var s = new DirectorySecurity();
        s.AddAccessRule(Rule(WellKnownSidType.BuiltinUsersSid,
            FileSystemRights.CreateFiles | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes,
            InheritanceFlags.None));
        s.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null),
            FileSystemRights.FullControl, InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
        return s;
    }

    private static FileSystemAccessRule Rule(WellKnownSidType sid, FileSystemRights rights, InheritanceFlags inherit) =>
        new(new SecurityIdentifier(sid, null), rights, inherit, PropagationFlags.None, AccessControlType.Allow);

    private static bool IsLink(FileSystemInfo info) => (info.Attributes & FileAttributes.ReparsePoint) != 0;

    private static void RemoveLinks(string dir, List<string> notes)
    {
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            if (!IsLink(entry)) continue;
            // Deleting a junction or symlink removes the link itself, never what it points to.
            if (entry is DirectoryInfo d) d.Delete(recursive: false); else entry.Delete();
            notes.Add($"Removed {entry.FullName}: a link in Homebase's folder, which only the installer and service create.");
        }
    }
}
