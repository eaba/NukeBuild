#region Using Directives

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.CI.AzurePipelines;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Coverlet;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.MSBuild;
using Nuke.Common.Tools.NuGet;
using Nuke.Common.Tools.ReportGenerator;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.IO.CompressionTasks;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.Tools.MSBuild.MSBuildTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.NuGet.NuGetTasks;
using static Nuke.Common.Tools.ReportGenerator.ReportGeneratorTasks;

#endregion

[CheckBuildProjectConfigurations]
[UnsetVisualStudioEnvironmentVariables]
[AzurePipelines(
    AzurePipelinesImage.WindowsLatest,
    InvokedTargets = new[] {nameof(Pack)},
    NonEntryTargets = new[] {nameof(Restore), nameof(Compile), nameof(Test), nameof(Coverage)},
    ExcludedTargets = new[] {nameof(Clean)})]
class Build : NukeBuild
{
    /// Support plugins are available for:
    /// - JetBrains ReSharper        https://nuke.build/resharper
    /// - JetBrains Rider            https://nuke.build/rider
    /// - Microsoft VisualStudio     https://nuke.build/visualstudio
    /// - Microsoft VSCode           https://nuke.build/vscode
    public static int Main() => Execute<Build>(x => x.Pack);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Solution] readonly Solution Solution;
    [GitRepository] readonly GitRepository GitRepository;
    [GitVersion(Framework = "netcoreapp3.1")] readonly GitVersion GitVersion;
    [CI] readonly AzurePipelines AzurePipelines;

    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath TestsDirectory => RootDirectory / "tests";
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

    AbsolutePath TestOutputDirectory => RootDirectory / "build" / Configuration / "x64";

    AbsolutePath TestResultDirectory => ArtifactsDirectory / "test-results";

    AbsolutePath NuspecDirectory => RootDirectory / "Nuspec";

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
            TestsDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
            EnsureCleanDirectory(ArtifactsDirectory);
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            MSBuild(s => s
                .SetTargetPath(Solution)
                .SetTargets("Restore"));
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            MSBuild(s => s
                .SetTargetPath(Solution)
                .SetTargets("Rebuild")
                .SetConfiguration(Configuration)
                .SetAssemblyVersion(GitVersion.AssemblySemVer)
                .SetFileVersion(GitVersion.AssemblySemFileVer)
                .SetInformationalVersion(GitVersion.InformationalVersion)
                .SetMaxCpuCount(Environment.ProcessorCount)
                .SetNodeReuse(IsLocalBuild));
        });

    IEnumerable<Project> TestProjects => Solution.GetProjects("*.Tests");

    Target Test => _ => _
        .DependsOn(Compile)
        .Produces(TestResultDirectory / "*.trx")
        .Produces(TestResultDirectory / "*.xml")
        .Executes(() =>
        {
            DotNetTest(c => c
                    .SetConfiguration(Configuration)
                    .SetOutput(TestOutputDirectory)
                    .EnableNoBuild()
                    .SetResultsDirectory(TestResultDirectory)
                    .When(InvokedTargets.Contains(Coverage) || IsServerBuild, _ => _
                        .When(IsServerBuild, _ => _
                            .EnableUseSourceLink()))
                    .CombineWith(TestProjects, (settings, project) =>
                        settings.SetProjectFile(project)
                            .SetLogger($"trx;LogFileName={project.Name}.trx")
                            .CombineWith(project.GetTargetFrameworks(), (_, framework) => _
                                .SetFramework(framework)
                                .When(InvokedTargets.Contains(Coverage) || IsServerBuild, _ => _
                                    .EnableCollectCoverage()
                                    .SetCoverletOutputFormat(CoverletOutputFormat.cobertura)
                                    .SetExcludeByFile("*.Generated.cs")
                                    .SetCoverletOutput(TestResultDirectory / $"{project.Name}.xml")
                                    .When(IsServerBuild, _ => _
                                        .EnableUseSourceLink())))
                    )
                // .CombineWith(TestProjects, (_, v) => _
                //     .SetProjectFile(v)
                //     .SetLogger($"trx;LogFileName={v.Name}.trx")
                //     .When(InvokedTargets.Contains(Coverage) || IsServerBuild, _ => _
                //         .SetCoverletOutput(TestResultDirectory / $"{v.Name}.xml")))
            );

            TestResultDirectory.GlobFiles("*.trx").ForEach(x =>
                AzurePipelines?.PublishTestResults(
                    type: AzurePipelinesTestResultsType.VSTest,
                    title: $"{Path.GetFileNameWithoutExtension(x)} ({AzurePipelines.StageDisplayName})",
                    files: new string[] {x}));
        });

    string CoverageReportDirectory => ArtifactsDirectory / "coverage-report";
    string CoverageReportArchive => ArtifactsDirectory / "coverage-report.zip";

    Target Coverage => _ => _
        .DependsOn(Test)
        .TriggeredBy(Test)
        .Consumes(Test)
        .Produces(CoverageReportArchive)
        .Executes(() =>
        {
            var reportFiles = TestResultDirectory.GlobFiles("*.xml");
            if (!reportFiles.Any())
            {
                Console.WriteLine("No Coverage Report found! Skipping");
                return;
            }

            ReportGenerator(_ => _
                .SetReports(TestResultDirectory / "*.xml")
                .SetReportTypes(ReportTypes.HtmlInline)
                .SetTargetDirectory(CoverageReportDirectory)
                .SetFramework("netcoreapp3.0"));

            TestResultDirectory.GlobFiles("*.xml").ForEach(x =>
                AzurePipelines?.PublishCodeCoverage(
                    AzurePipelinesCodeCoverageToolType.Cobertura,
                    x,
                    CoverageReportDirectory));

            CompressZip(
                CoverageReportDirectory,
                CoverageReportArchive,
                fileMode: FileMode.Create);
        });

    Target Pack => _ => _
        .DependsOn(Coverage)
        .Produces(ArtifactsDirectory / "*.nupkg")
        .Executes(() =>
        {
            var nuspecFiles = NuspecDirectory.GlobFiles("*.nuspec");
            if (nuspecFiles.Any())
            {
                NuGetPack(_ => _
                    .SetBuild(InvokedTargets.Contains(Compile))
                    .SetConfiguration(Configuration)
                    .SetOutputDirectory(ArtifactsDirectory)
                    .SetVersion(GitVersion.NuGetVersionV2)
                    .SetSymbols(true)
                    .CombineWith(nuspecFiles, (settings, nuspec) => settings.SetTargetPath(nuspec)));
            }
            else
            {
                DotNetPack(_ => _
                    .SetProject(Solution)
                    .SetNoBuild(InvokedTargets.Contains(Compile))
                    .EnableNoBuild()
                    .SetIncludeSymbols(true)
                    .SetConfiguration(Configuration)
                    .SetOutputDirectory(ArtifactsDirectory)
                    .SetVersion(GitVersion.NuGetVersionV2));
            }
        });
}