using DN42Atlas.Reporting;

namespace DN42Atlas.Commands;

public static class ReportCommand
{
    public static async Task ExecuteAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine(
                "Usage: report <web-probe.json>");

            return;
        }

        var inputPath =
            args[1];

        if (!File.Exists(inputPath))
        {
            Console.WriteLine(
                $"File not found: {inputPath}");

            return;
        }

        var htmlPath =
            Path.ChangeExtension(
                inputPath,
                ".html");

        await AtlasReportGenerator.GenerateAsync(
            inputPath,
            htmlPath);

        Console.WriteLine(
            $"Atlas viewer written to: {htmlPath}");

        return;
    }
}
