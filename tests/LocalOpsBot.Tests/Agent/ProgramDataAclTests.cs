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
