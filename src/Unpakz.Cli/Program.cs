using System;
using System.Text;

namespace Unpakz.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);      // file names are Unicode; the default console code page is not
        return CliApp.Run(args, Console.Out, Console.Error);
    }
}
