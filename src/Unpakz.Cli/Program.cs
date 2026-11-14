using System;

namespace Unpakz.Cli;

internal static class Program
{
    private static int Main(string[] args) => CliApp.Run(args, Console.Out, Console.Error);
}
