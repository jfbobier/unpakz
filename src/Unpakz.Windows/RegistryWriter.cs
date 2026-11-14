using System;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Unpakz.Engine;

namespace Unpakz.Windows;

/// <summary>HKEY_CURRENT_USER implementation of <see cref="IRegistryWriter"/>. An optional prefix puts everything under a scratch key (used by tests).</summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryWriter(string prefix = "") : IRegistryWriter
{
    private string Full(string key) => prefix.Length == 0 ? key : prefix.TrimEnd('\\') + "\\" + key;

    public void SetValue(string key, string? name, string value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Full(key), writable: true);
        k.SetValue(name ?? string.Empty, value, RegistryValueKind.String);
    }

    public void DeleteValue(string key, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Full(key), writable: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }

    public void DeleteKeyTree(string key) => Registry.CurrentUser.DeleteSubKeyTree(Full(key), throwOnMissingSubKey: false);

    /// <summary>Removes the whole scratch prefix (tests).</summary>
    public void DeletePrefix()
    {
        if (prefix.Length > 0) Registry.CurrentUser.DeleteSubKeyTree(prefix, throwOnMissingSubKey: false);
    }
}
