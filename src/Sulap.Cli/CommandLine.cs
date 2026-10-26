using Sulap.Core;
using Sulap.Core.Formatting;

namespace Sulap.Cli;

/// <summary>The <c>sulap</c> command: a terminal front end to the engine in Sulap.Core.</summary>
internal static class CommandLine
{
    public const int Success = 0;
    public const int UsageError = 1;
    public const int Failure = 2;

    private static string Command => AppIdentity.Name.ToLowerInvariant();

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "help" or "-h" or "--help")
        {
            PrintUsage(output);
            return args.Length == 0 ? UsageError : Success;
        }

        switch (args[0])
        {
            case "--version" or "-v":
                output.WriteLine($"{Command} {AppIdentity.Version}");
                return Success;

            case "info" when args.Length == 2:
                return Info(args[1], output, error);

            case "convert" or "tool":
                error.WriteLine($"'{args[0]}' isn't available yet: conversions arrive in Phase 3 and tools in Phase 4.");
                return Failure;

            default:
                error.WriteLine($"Unknown command or wrong arguments: {string.Join(' ', args)}");
                PrintUsage(error);
                return UsageError;
        }
    }

    private static int Info(string path, TextWriter output, TextWriter error)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            error.WriteLine($"File not found: {file.FullName}");
            return Failure;
        }

        output.WriteLine($"Name:      {file.Name}");
        output.WriteLine($"Folder:    {file.DirectoryName}");
        output.WriteLine($"Size:      {FileSize.Format(file.Length)} ({file.Length:N0} bytes)");
        output.WriteLine($"Modified:  {file.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
        output.WriteLine($"Extension: {(file.Extension.Length > 0 ? file.Extension : "(none)")}");
        return Success;
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine($"{AppIdentity.Name} {AppIdentity.Version} — convert files from the terminal.");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine($"  {Command} info <file>                 Show what {AppIdentity.Name} knows about a file");
        writer.WriteLine($"  {Command} convert <file> --to <fmt>   Convert a file (Phase 3)");
        writer.WriteLine($"  {Command} tool <name> <file>          Run a tool such as compress (Phase 4)");
        writer.WriteLine($"  {Command} --version                   Print the version");
    }
}
