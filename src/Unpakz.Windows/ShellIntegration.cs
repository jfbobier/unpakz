using System;

using Unpakz.Interop;

namespace Unpakz.Windows;

/// <summary>Registers the running executable as a handler for .zip and .rar (per user, no administrator rights).</summary>
public static class ShellIntegration
{
    public static void Register(string exePath, IRegistryWriter? registry = null) =>
        FileAssociationPlan.Build(exePath).Apply(registry ?? new RegistryWriter());

    public static void Unregister(IRegistryWriter? registry = null) =>
        FileAssociationPlan.Remove(registry ?? new RegistryWriter());
}
