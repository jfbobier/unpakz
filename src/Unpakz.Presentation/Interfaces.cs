namespace Unpakz.Presentation;

/// <summary>Asks the user for a destination folder (a native folder dialog in the app, a fake in tests). Returns null if they cancel.</summary>
public interface IFolderPicker
{
    string? Pick(string title, string? startFolder);
}

/// <summary>Questions the app must put to the user mid-operation.</summary>
public interface IPrompt
{
    /// <summary>Some files already exist at the destination. True to replace them.</summary>
    bool ConfirmReplace(string message);
}
