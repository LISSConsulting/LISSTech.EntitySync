using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Runtime;

public sealed class EntitySyncProfileInfo
{
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public string[] Vendors { get; set; } = Array.Empty<string>();
    public string Path { get; set; } = string.Empty;
}

public sealed class EntitySyncStoredVendorProfile
{
    public string Vendor { get; set; } = string.Empty;
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class EntitySyncProfileStore
{
    private const string ProfilePathEnvironmentVariable = "LISSTECH_ENTITYSYNC_PROFILE_PATH";
    private const string ProtectionPurpose = "LISSTech.EntitySync.Profile.v1";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(ProtectionPurpose);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string ProfilePath
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable(ProfilePathEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(overridePath)) return Path.GetFullPath(overridePath);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "LISSTech", "EntitySync", "profiles.json");
        }
    }

    public static void SaveVendor(string profileName, string vendor, IReadOnlyDictionary<string, string?> settings, bool makeDefault)
    {
        profileName = NormalizeProfileName(profileName);
        vendor = EntitySyncVendors.Normalize(vendor);
        var document = ReadDocument();
        if (!document.Profiles.TryGetValue(profileName, out var profile))
        {
            profile = new StoredProfile { Name = profileName };
            document.Profiles[profileName] = profile;
        }

        var cleanSettings = settings
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase);

        profile.Vendors[vendor] = new StoredVendorProfile
        {
            Vendor = vendor,
            ProtectedSettings = Protect(cleanSettings)
        };

        if (makeDefault || string.IsNullOrWhiteSpace(document.DefaultProfile)) document.DefaultProfile = profileName;
        WriteDocument(document);
    }

    public static IReadOnlyList<EntitySyncProfileInfo> ListProfiles()
    {
        var document = ReadDocument();
        return document.Profiles.Values
            .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .Select(profile => new EntitySyncProfileInfo
            {
                Name = profile.Name,
                IsDefault = profile.Name.Equals(document.DefaultProfile, StringComparison.OrdinalIgnoreCase),
                Vendors = profile.Vendors.Keys.OrderBy(vendor => vendor, StringComparer.OrdinalIgnoreCase).ToArray(),
                Path = ProfilePath
            })
            .ToArray();
    }

    public static IReadOnlyList<EntitySyncStoredVendorProfile> LoadProfile(string? profileName)
    {
        var document = ReadDocument();
        profileName = string.IsNullOrWhiteSpace(profileName) ? document.DefaultProfile : NormalizeProfileName(profileName);
        if (string.IsNullOrWhiteSpace(profileName)) throw new InvalidOperationException("No EntitySync profile was specified and no default profile is configured.");
        if (!document.Profiles.TryGetValue(profileName, out var profile)) throw new InvalidOperationException($"EntitySync profile '{profileName}' was not found.");
        return profile.Vendors.Values
            .OrderBy(vendor => vendor.Vendor, StringComparer.OrdinalIgnoreCase)
            .Select(vendor => new EntitySyncStoredVendorProfile
            {
                Vendor = vendor.Vendor,
                Settings = Unprotect(vendor.ProtectedSettings)
            })
            .ToArray();
    }

    public static void RemoveProfile(string profileName)
    {
        profileName = NormalizeProfileName(profileName);
        var document = ReadDocument();
        if (!document.Profiles.Remove(profileName)) throw new InvalidOperationException($"EntitySync profile '{profileName}' was not found.");
        if (profileName.Equals(document.DefaultProfile, StringComparison.OrdinalIgnoreCase)) document.DefaultProfile = document.Profiles.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        WriteDocument(document);
    }

    public static void SetDefaultProfile(string profileName)
    {
        profileName = NormalizeProfileName(profileName);
        var document = ReadDocument();
        if (!document.Profiles.ContainsKey(profileName)) throw new InvalidOperationException($"EntitySync profile '{profileName}' was not found.");
        document.DefaultProfile = profileName;
        WriteDocument(document);
    }

    private static string NormalizeProfileName(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) throw new InvalidOperationException("Profile is required.");
        profileName = profileName.Trim();
        if (profileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidOperationException("Profile contains invalid file-name characters.");
        return profileName;
    }

    private static StoredProfilesDocument ReadDocument()
    {
        var path = ProfilePath;
        if (!File.Exists(path)) return new StoredProfilesDocument();
        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json)) return new StoredProfilesDocument();
        return JsonSerializer.Deserialize<StoredProfilesDocument>(json) ?? new StoredProfilesDocument();
    }

    private static void WriteDocument(StoredProfilesDocument document)
    {
        var path = ProfilePath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(document, JsonOptions), new UTF8Encoding(false));
    }

    private static string Protect(IReadOnlyDictionary<string, string> settings)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("EntitySync DPAPI profiles are supported only on Windows. Use environment variables or 1Password secret injection on this platform.");
        var json = JsonSerializer.Serialize(settings);
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static Dictionary<string, string> Unprotect(string protectedSettings)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("EntitySync DPAPI profiles are supported only on Windows. Use environment variables or 1Password secret injection on this platform.");
        var bytes = Convert.FromBase64String(protectedSettings);
        var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser));
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StoredProfilesDocument
    {
        public string? DefaultProfile { get; set; }
        public Dictionary<string, StoredProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StoredProfile
    {
        public string Name { get; set; } = string.Empty;
        public Dictionary<string, StoredVendorProfile> Vendors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StoredVendorProfile
    {
        public string Vendor { get; set; } = string.Empty;
        public string ProtectedSettings { get; set; } = string.Empty;
    }
}
