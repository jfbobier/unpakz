using System;
using System.Collections.Generic;

namespace Unpakz.Interop;

/// <summary>Abstraction over the Windows registry (HKEY_CURRENT_USER) so the registration logic is testable anywhere.</summary>
public interface IRegistryWriter
{
    void SetValue(string key, string? name, string value);

    void DeleteValue(string key, string name);

    void DeleteKeyTree(string key);
}

public sealed class RegistryWrite
{
    public RegistryWrite(string key, string? name, string value)
    {
        Key = key;
        Name = name;
        Value = value;
    }

    public string Key { get; }

    public string? Name { get; }

    public string Value { get; }
}

/// <summary>
/// What registering the app as an archive handler writes: everything under HKCU (no administrator rights), making Unpakz appear in
/// "Open with" and in Settings > Default apps. It never writes the protected UserChoice keys: the user picks the default.
/// </summary>
public sealed class FileAssociationPlan
{
    private const string ProgId = "Unpakz.Archive";
    private const string AppKey = @"Software\Classes\Applications\Unpakz.exe";
    private static readonly string[] Extensions = { ".zip", ".rar", ".tgz" };

    private FileAssociationPlan(IReadOnlyList<RegistryWrite> writes) => Writes = writes;

    public IReadOnlyList<RegistryWrite> Writes { get; }

    public static FileAssociationPlan Build(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) throw new ArgumentException("An executable path is required.", nameof(exePath));
        if (exePath.IndexOf('"') >= 0) throw new ArgumentException("The executable path must not contain quotes.", nameof(exePath));
        var drive = exePath.Length > 3 && ((exePath[0] >= 'a' && exePath[0] <= 'z') || (exePath[0] >= 'A' && exePath[0] <= 'Z')) && exePath[1] == ':' && exePath[2] == '\\';
        if (!drive && !exePath.StartsWith(@"\\", StringComparison.Ordinal)) throw new ArgumentException("An absolute Windows path is required.", nameof(exePath));

        var command = "\"" + exePath + "\" \"%1\"";
        var w = new List<RegistryWrite>
        {
            new(@"Software\Classes\" + ProgId, null, "Archive"),
            new(@"Software\Classes\" + ProgId + @"\DefaultIcon", null, "\"" + exePath + "\",0"),
            new(@"Software\Classes\" + ProgId + @"\shell\open\command", null, command),
            new(AppKey + @"\shell\open\command", null, command),
            new(AppKey, "FriendlyAppName", "Unpakz"),
            new(@"Software\Unpakz\Capabilities", "ApplicationName", "Unpakz"),
            new(@"Software\Unpakz\Capabilities", "ApplicationDescription", "Fast, safe, extraction-only ZIP, RAR and tar.gz tool."),
            new(@"Software\RegisteredApplications", "Unpakz", @"Software\Unpakz\Capabilities"),
        };

        foreach (var ext in Extensions)
        {
            w.Add(new(@"Software\Unpakz\Capabilities\FileAssociations", ext, ProgId));
            w.Add(new(@"Software\Classes\" + ext + @"\OpenWithProgids", ProgId, string.Empty));
            w.Add(new(AppKey + @"\SupportedTypes", ext, string.Empty));
        }

        return new FileAssociationPlan(w);
    }

    public void Apply(IRegistryWriter registry)
    {
        if (registry is null) throw new ArgumentNullException(nameof(registry));
        foreach (var write in Writes) registry.SetValue(write.Key, write.Name, write.Value);
    }

    public static void Remove(IRegistryWriter registry)
    {
        if (registry is null) throw new ArgumentNullException(nameof(registry));
        foreach (var ext in Extensions) registry.DeleteValue(@"Software\Classes\" + ext + @"\OpenWithProgids", ProgId);
        registry.DeleteValue(@"Software\RegisteredApplications", "Unpakz");
        registry.DeleteKeyTree(@"Software\Classes\" + ProgId);
        registry.DeleteKeyTree(AppKey);
        registry.DeleteKeyTree(@"Software\Unpakz");
    }
}
