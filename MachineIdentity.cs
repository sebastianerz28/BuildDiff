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
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            // On some Linux/container setups ApplicationData resolves to "" (no HOME/XDG),
            // which would make the salt path relative and thus vary by cwd — breaking the
            // "stable across re-captures" contract. Fall back to a stable unsalted id.
            if (string.IsNullOrEmpty(appData) || !Path.IsPathRooted(appData))
                return Hash("nosalt:" + Environment.MachineName);

            var dir = Path.Combine(appData, "builddiff");
            var saltPath = Path.Combine(dir, "salt");
            var salt = File.Exists(saltPath) ? File.ReadAllText(saltPath).Trim() : "";
            if (salt.Length == 0) salt = Persist(dir, saltPath);
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
        try
        {
            // CreateNew so concurrent first-runs don't clobber each other; the loser adopts
            // the winner's salt instead of writing a divergent one.
            using var fs = new FileStream(saltPath, FileMode.CreateNew, FileAccess.Write);
            using var w = new StreamWriter(fs);
            w.Write(salt);
            return salt;
        }
        catch (IOException)
        {
            var existing = File.ReadAllText(saltPath).Trim();
            return existing.Length > 0 ? existing : salt;
        }
    }

    private static string Hash(string s)
        => "m_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();
}
