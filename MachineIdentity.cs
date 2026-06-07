using System.Security.Cryptography;
using System.Text;

namespace BuildDiff;

/// <summary>
/// A stable, pseudonymous machine id for drift correlation: a salted hash of the
/// hostname. The salt is generated once and persisted in the CLI config dir so the
/// id is stable across re-captures but NOT reversible to the hostname by a server.
/// The raw hostname is never used as the identity key.
/// </summary>
public static class MachineIdentity
{
    public static string Get()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "builddiff");
            var saltPath = Path.Combine(dir, "salt");
            string salt;
            if (File.Exists(saltPath))
            {
                salt = File.ReadAllText(saltPath).Trim();
                if (salt.Length == 0) salt = Persist(dir, saltPath);
            }
            else
            {
                salt = Persist(dir, saltPath);
            }
            return Hash(salt + ":" + Environment.MachineName);
        }
        catch
        {
            // Config dir unwritable — deterministic but unsalted fallback (still stable per host).
            return Hash("nosalt:" + Environment.MachineName);
        }
    }

    private static string Persist(string dir, string saltPath)
    {
        Directory.CreateDirectory(dir);
        var salt = Guid.NewGuid().ToString("N");
        File.WriteAllText(saltPath, salt);
        return salt;
    }

    private static string Hash(string s)
        => "m_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();
}
