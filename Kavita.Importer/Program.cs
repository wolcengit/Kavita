using Kavita.Importer.Cli;
using Kavita.Importer.Import;

namespace Kavita.Importer;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = CliParser.Parse(args, out var error);
        if (options is null)
        {
            if (error is null)
            {
                Console.WriteLine(CliParser.HelpText);
                return 0;
            }

            Console.Error.WriteLine($"错误: {error}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(CliParser.HelpText);
            return 2;
        }

        return await ImporterRunner.RunAsync(options);
    }
}
