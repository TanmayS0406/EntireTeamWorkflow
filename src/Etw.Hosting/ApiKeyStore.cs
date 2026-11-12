using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Etw.Shared;

namespace Etw.Hosting;

/// <summary>
/// The API key is a random value stored in %ProgramData%\EntireTeamWorkflow\api.key. Anything that can read that file may
/// call the services, so the file is restricted to SYSTEM, Administrators and the account that created it (the installer
/// also grants the desktop user). It stops other local users and web pages from driving agents that edit files.
/// </summary>
public static class ApiKeyStore
{
    public static string GetOrCreate()
    {
        var path = EtwPaths.ApiKeyFile;
        Directory.CreateDirectory(EtwPaths.Root);

        if (!File.Exists(path))
        {
            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            try
            {
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    stream.Write(Encoding.ASCII.GetBytes(key));
                }
                FileAcl.Restrict(new FileInfo(path));
            }
            catch (IOException)
            {
                // Another service created it first: read theirs below.
            }
        }

        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                var value = File.ReadAllText(path).Trim();
                if (value.Length >= 32) return value;
            }
            catch (IOException)
            {
                // Still being written.
            }
            Thread.Sleep(50);
        }
        throw new InvalidOperationException($"Could not read the API key at {path}.");
    }

    /// <summary>Reads the key from a client process (the desktop app). Throws a readable error when access is denied.</summary>
    public static string Read()
    {
        try
        {
            return File.ReadAllText(EtwPaths.ApiKeyFile).Trim();
        }
        catch (FileNotFoundException)
        {
            throw new InvalidOperationException("The Entire Team Workflow backend has not been started yet (no API key file). Start the EtwBackend service.");
        }
        catch (DirectoryNotFoundException)
        {
            throw new InvalidOperationException("The Entire Team Workflow backend has not been started yet (no API key file). Start the EtwBackend service.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Access to {EtwPaths.ApiKeyFile} was denied. Re-run scripts\\Install-Services.ps1 so it grants your account read access.");
        }
    }
}

public static class FileAcl
{
    /// <summary>Removes inherited permissions and grants full control to SYSTEM, Administrators and the current account only.</summary>
    public static void Restrict(FileSystemInfo target)
    {
        if (!OperatingSystem.IsWindows()) return;

        var security = target is DirectoryInfo directory ? new DirectorySecurity() : (FileSystemSecurity)new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var identities = new List<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
        };
        if (WindowsIdentity.GetCurrent().User is { } self) identities.Add(self);

        var inherit = target is DirectoryInfo ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        foreach (var sid in identities.Distinct())
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        switch (target)
        {
            case DirectoryInfo d: d.SetAccessControl((DirectorySecurity)security); break;
            case FileInfo f: f.SetAccessControl((FileSecurity)security); break;
        }
    }
}
