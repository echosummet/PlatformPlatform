using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using JetBrains.Annotations;
using PlatformPlatform.DeveloperCli.Utilities;
using Spectre.Console;
using Environment = PlatformPlatform.DeveloperCli.Installation.Environment;

namespace PlatformPlatform.DeveloperCli.Commands;

[UsedImplicitly]
public class CodeCoverage : Command
{
    public CodeCoverage() : base("code-coverage", "Run JetBrains Code Coverage")
    {
        Handler = CommandHandler.Create(Execute);
    }

    private int Execute()
    {
        var workingDirectory = new DirectoryInfo(Path.Combine(Environment.SolutionFolder, "..", "application"))
            .FullName;

        ProcessHelper.StartProcess("dotnet", "tool restore", workingDirectory);
        ProcessHelper.StartProcess("dotnet", "build", workingDirectory);
        ProcessHelper.StartProcess(
            "dotnet",
            "dotcover test PlatformPlatform.sln --no-build --dcOutput=coverage/dotCover.html --dcReportType=HTML --dcFilters=\"+:PlatformPlatform.*;-:*.Tests;-:type=*.AppHost.*\"",
            workingDirectory
        );

        var codeCoverageReport = Path.Combine(workingDirectory, "coverage", "dotCover.html");
        AnsiConsole.MarkupLine($"[green]Code Coverage Report[/] {codeCoverageReport}");
        ProcessHelper.StartProcess("open", codeCoverageReport, workingDirectory);

        return 0;
    }
}