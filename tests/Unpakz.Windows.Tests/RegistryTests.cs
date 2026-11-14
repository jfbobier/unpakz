using Microsoft.Win32;
using Unpakz.Interop;
using Unpakz.Windows;

namespace Unpakz.Windows.Tests;

public sealed class RegistryTests : IDisposable
{
    private readonly string _prefix = @"Software\UnpakzTests\" + Guid.NewGuid().ToString("N");
    private readonly RegistryWriter _writer;

    public RegistryTests() => _writer = new RegistryWriter(_prefix);

    public void Dispose()
    {
        _writer.DeletePrefix();
        using var parent = Registry.CurrentUser.OpenSubKey(@"Software\UnpakzTests", writable: true);
        if (parent is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(@"Software\UnpakzTests", false);
    }

    private string? Read(string key, string? name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(_prefix + "\\" + key);
        return k?.GetValue(name ?? string.Empty) as string;
    }

    [Fact]
    public void The_plan_really_lands_in_the_registry_and_really_goes_away()
    {
        const string exe = @"C:\Program Files\Unpakz\Unpakz.exe";
        var plan = FileAssociationPlan.Build(exe);
        plan.Apply(_writer);
        plan.Apply(_writer);

        Assert.Equal($"\"{exe}\" \"%1\"", Read(@"Software\Classes\Unpakz.Archive\shell\open\command", null));
        Assert.Equal("Unpakz.Archive", Read(@"Software\Unpakz\Capabilities\FileAssociations", ".rar"));
        Assert.Equal("", Read(@"Software\Classes\.zip\OpenWithProgids", "Unpakz.Archive"));

        FileAssociationPlan.Remove(_writer);
        Assert.Null(Read(@"Software\Classes\Unpakz.Archive\shell\open\command", null));
        Assert.Null(Read(@"Software\Unpakz\Capabilities\FileAssociations", ".rar"));
        Assert.Null(Read(@"Software\RegisteredApplications", "Unpakz"));
        Assert.Null(Read(@"Software\Classes\.zip\OpenWithProgids", "Unpakz.Archive"));
    }

    [Fact]
    public void Deleting_things_that_are_not_there_is_not_an_error_and_unrelated_values_survive()
    {
        _writer.SetValue(@"Software\Other", "keep", "me");
        _writer.DeleteValue(@"Software\Nope", "x");
        _writer.DeleteKeyTree(@"Software\AlsoNope");
        FileAssociationPlan.Remove(_writer);
        Assert.Equal("me", Read(@"Software\Other", "keep"));
    }
}
