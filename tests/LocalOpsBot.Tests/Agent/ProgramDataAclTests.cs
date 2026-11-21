using System.Security.AccessControl;
using System.Security.Principal;
using LocalOpsBot.Agent.Services;
using Xunit;

namespace LocalOpsBot.Tests.Agent;

/// <summary>Homebase's ProgramData folder: standard users read, and only create files in logs\.</summary>
public sealed class ProgramDataAclTests
{
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    private static IEnumerable<FileSystemAccessRule> Rules(DirectorySecurity s) =>
        s.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();

    private const FileSystemRights AnyWrite = FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories
        | FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.ChangePermissions
        | FileSystemRights.TakeOwnership | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles;

    [Fact]
    public void The_root_lets_users_read_only_and_inherits_nothing()
    {
        var s = ProgramDataAcl.RootSecurity();

        Assert.True(s.AreAccessRulesProtected);
        var users = Assert.Single(Rules(s), r => r.IdentityReference.Equals(Users));
        Assert.Equal(AccessControlType.Allow, users.AccessControlType);
        Assert.Equal(0, (int)(users.FileSystemRights & AnyWrite));
    }

    [Fact]
    public void Logs_lets_users_create_files_but_not_folders()
    {
        var users = Assert.Single(Rules(ProgramDataAcl.LogsSecurity()), r => r.IdentityReference.Equals(Users));

        Assert.True(users.FileSystemRights.HasFlag(FileSystemRights.CreateFiles));
        // CreateDirectories is the bit a junction needs.
        Assert.False(users.FileSystemRights.HasFlag(FileSystemRights.CreateDirectories));
        Assert.Equal(InheritanceFlags.None, users.InheritanceFlags);
    }

    [Fact]
    public void A_folder_that_already_has_the_rule_is_not_written_again()
    {
        var dir = Directory.CreateTempSubdirectory("homebase-acl-");
        var logs = dir.CreateSubdirectory("logs");
        try
        {
            Assert.False(ProgramDataAcl.AlreadyApplied(dir.GetAccessControl(), ProgramDataAcl.RootSecurity()));
            Assert.False(ProgramDataAcl.AlreadyApplied(logs.GetAccessControl(), ProgramDataAcl.LogsSecurity()));

            // As Windows stores them and reads them back: then they match and the start-up check skips the write.
            dir.SetAccessControl(ProgramDataAcl.RootSecurity());
            logs.SetAccessControl(ProgramDataAcl.LogsSecurity());

            Assert.True(ProgramDataAcl.AlreadyApplied(dir.GetAccessControl(), ProgramDataAcl.RootSecurity()));
            Assert.True(ProgramDataAcl.AlreadyApplied(logs.GetAccessControl(), ProgramDataAcl.LogsSecurity()));
            Assert.False(ProgramDataAcl.AlreadyApplied(logs.GetAccessControl(), ProgramDataAcl.RootSecurity()));
        }
        finally
        {
            // The owner may always set the ACL again: give this user the folders back, then delete them.
            OpenToMe(dir);
            OpenToMe(logs);
            Directory.Delete(dir.FullName, recursive: true);
        }
    }

    private static void OpenToMe(DirectoryInfo dir)
    {
        var s = new DirectorySecurity();
        s.AddAccessRule(new FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(s);
    }

    [Fact]
    public void Outside_the_service_it_changes_nothing()
    {
        var dir = Directory.CreateTempSubdirectory("homebase-acl-").FullName;
        try
        {
            var before = new DirectoryInfo(dir).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            Assert.Empty(ProgramDataAcl.Restrict(dir));
            Assert.Equal(before, new DirectoryInfo(dir).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
