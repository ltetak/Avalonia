using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Nuke.Common;
using Nuke.Common.Git;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.MSBuild;
using Nuke.Common.Tools.Npm;
using Nuke.Common.Utilities;
using Nuke.Common.Utilities.Collections;
using Pharmacist.Core;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.MSBuild.MSBuildTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.Xunit.XunitTasks;
using static Nuke.Common.Tools.VSWhere.VSWhereTasks;
using System.IO.Compression;

/*
 Before editing this file, install support plugin for your IDE,
 running and debugging a particular target (optionally without deps) would be way easier
 ReSharper/Rider - https://plugins.jetbrains.com/plugin/10803-nuke-support
 VSCode - https://marketplace.visualstudio.com/items?itemName=nuke.support

 */

partial class Build : NukeBuild
{
    [Solution("Avalonia.sln")] readonly Solution Solution;

    static Lazy<string> MsBuildExe = new Lazy<string>(() =>
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;

        var msBuildDirectory = VSWhere("-latest -nologo -property installationPath -format value -prerelease").FirstOrDefault().Text;

        if (!string.IsNullOrWhiteSpace(msBuildDirectory))
        {
            string msBuildExe = Path.Combine(msBuildDirectory, @"MSBuild\Current\Bin\MSBuild.exe");
            if (!System.IO.File.Exists(msBuildExe))
                msBuildExe = Path.Combine(msBuildDirectory, @"MSBuild\15.0\Bin\MSBuild.exe");

            return msBuildExe;
        }

        return null;
    }, false);

    BuildParameters Parameters { get; set; }
    protected override void OnBuildInitialized()
    {
        Parameters = new BuildParameters(this);
        Information("Building version {0} of Avalonia ({1}) using version {2} of Nuke.",
            Parameters.Version,
            Parameters.Configuration,
            typeof(NukeBuild).Assembly.GetName().Version.ToString());

        if (Parameters.IsLocalBuild)
        {
            Information("Repository Name: " + Parameters.RepositoryName);
            Information("Repository Branch: " + Parameters.RepositoryBranch);
        }
        Information("Configuration: " + Parameters.Configuration);
        Information("IsLocalBuild: " + Parameters.IsLocalBuild);
        Information("IsRunningOnUnix: " + Parameters.IsRunningOnUnix);
        Information("IsRunningOnWindows: " + Parameters.IsRunningOnWindows);
        Information("IsRunningOnAzure:" + Parameters.IsRunningOnAzure);
        Information("IsPullRequest: " + Parameters.IsPullRequest);
        Information("IsMainRepo: " + Parameters.IsMainRepo);
        Information("IsMasterBranch: " + Parameters.IsMasterBranch);
        Information("IsReleaseBranch: " + Parameters.IsReleaseBranch);
        Information("IsReleasable: " + Parameters.IsReleasable);
        Information("IsMyGetRelease: " + Parameters.IsMyGetRelease);
        Information("IsNuGetRelease: " + Parameters.IsNuGetRelease);

        void ExecWait(string preamble, string command, string args)
        {
            Console.WriteLine(preamble);
            Process.Start(new ProcessStartInfo(command, args) {UseShellExecute = false}).WaitForExit();
        }
        ExecWait("dotnet version:", "dotnet", "--version");
    }

    IReadOnlyCollection<Output> MsBuildCommon(
        string projectFile,
        Configure<MSBuildSettings> configurator = null)
    {
        return MSBuild(c => c
            .SetProjectFile(projectFile)
            // This is required for VS2019 image on Azure Pipelines
            .When(Parameters.IsRunningOnWindows &&
                  Parameters.IsRunningOnAzure, _ => _
                .AddProperty("JavaSdkDirectory", GetVariable<string>("JAVA_HOME_8_X64")))
            .AddProperty("PackageVersion", Parameters.Version)
            .AddProperty("iOSRoslynPathHackRequired", true)
            .SetProcessToolPath(MsBuildExe.Value)
            .SetConfiguration(Parameters.Configuration)
            .SetVerbosity(MSBuildVerbosity.Minimal)
            .Apply(configurator));
    }

    Target Clean => _ => _.Executes(() =>
    {
        void safe(Action action)
        {
            try
            {
                action();
            }
            catch (Exception e) { Logger.Warn(e); }
        }
        //helps local dev builds
        void deldir(string dir) => safe(() => DeleteDirectory(dir));
        void cleandir(string dir) => safe(() => EnsureCleanDirectory(dir));

        Parameters.BuildDirs.ForEach(deldir);
        Parameters.BuildDirs.ForEach(cleandir);
        EnsureCleanDirectory(Parameters.ArtifactsDir);
        EnsureCleanDirectory(Parameters.NugetIntermediateRoot);
        EnsureCleanDirectory(Parameters.NugetRoot);
        EnsureCleanDirectory(Parameters.ZipRoot);
        EnsureCleanDirectory(Parameters.TestResultsRoot);
    });

    Target CompileHtmlPreviewer => _ => _
        .DependsOn(Clean)
        .OnlyWhenStatic(() => !Parameters.SkipPreviewer)
        .Executes(() =>
        {
            var webappDir = RootDirectory / "src" / "Avalonia.DesignerSupport" / "Remote" / "HtmlTransport" / "webapp";

            NpmTasks.NpmInstall(c => c
                .SetProcessWorkingDirectory(webappDir)
                .SetProcessArgumentConfigurator(a => a.Add("--silent")));
            NpmTasks.NpmRun(c => c
                .SetProcessWorkingDirectory(webappDir)
                .SetCommand("dist"));
        });

    Target CompileNative => _ => _
        .DependsOn(Clean)
        .DependsOn(GenerateCppHeaders)
        .OnlyWhenStatic(() => EnvironmentInfo.IsOsx)
        .Executes(() =>
        {
            var project = $"{RootDirectory}/native/Avalonia.Native/src/OSX/Avalonia.Native.OSX.xcodeproj/";
            var args = $"-project {project} -configuration {Parameters.Configuration} CONFIGURATION_BUILD_DIR={RootDirectory}/Build/Products/Release";
            ProcessTasks.StartProcess("xcodebuild", args).AssertZeroExitCode();
        });

    bool IsDotnetCoreOnlyBuild()
    {
        //avalonia can't build with msbuild from vs 2019 so we need vs 2022
        var r = int.Parse(VSWhere("-latest -nologo -property catalog_productLineVersion").First().Text);
        return ForceDotNetCoreBuild || (r <= 2019);
    }

    Target Compile => _ => _
        .DependsOn(Clean, CompileNative)
        .DependsOn(DownloadAvaloniaNativeLib)
        .DependsOn(CompileHtmlPreviewer)
        .Executes(async () =>
        {
            if (Parameters.IsRunningOnWindows && !IsDotnetCoreOnlyBuild())
                MsBuildCommon(Parameters.MSBuildSolution, c => c
                    .SetProcessArgumentConfigurator(a => a.Add("/r"))
                    .AddTargets("Build")
                );

            else
                DotNetBuild(c => c
                    .SetProjectFile(Parameters.MSBuildSolution)
                    .AddProperty("PackageVersion", Parameters.Version)
                    .SetConfiguration(Parameters.Configuration)
                );

            await CompileReactiveEvents();
        });

    async Task CompileReactiveEvents()
    {
        var avaloniaBuildOutput = Path.Combine(RootDirectory, "packages", "Avalonia", "bin", Parameters.Configuration);
        var avaloniaAssemblies = GlobFiles(avaloniaBuildOutput, "**/Avalonia*.dll")
            .Where(file => !file.Contains("Avalonia.Build.Tasks") &&
                            !file.Contains("Avalonia.Remote.Protocol"));

        var eventsDirectory = GlobDirectories($"{RootDirectory}/src/**/Avalonia.ReactiveUI.Events").First();
        var eventsBuildFile = Path.Combine(eventsDirectory, "Events_Avalonia.cs");
        if (File.Exists(eventsBuildFile))
            File.Delete(eventsBuildFile);

        using (var stream = File.Create(eventsBuildFile))
        using (var writer = new StreamWriter(stream))
        {
            await ObservablesForEventGenerator.ExtractEventsFromAssemblies(
                writer, avaloniaAssemblies, new string[0], "netstandard2.0"
            );
        }

        var eventsProject = Path.Combine(eventsDirectory, "Avalonia.ReactiveUI.Events.csproj");
        if (Parameters.IsRunningOnWindows && !IsDotnetCoreOnlyBuild())
            MsBuildCommon(eventsProject, c => c
                .SetProcessArgumentConfigurator(a => a.Add("/r"))
                .AddTargets("Build")
            );
        else
            DotNetBuild(c => c
                .SetProjectFile(eventsProject)
                .AddProperty("PackageVersion", Parameters.Version)
                .SetConfiguration(Parameters.Configuration)
            );
    }

    void RunCoreTest(string projectName)
    {
        if(!projectName.EndsWith(".csproj"))
            projectName = Path.Combine("tests", projectName, System.IO.Path.GetFileName(projectName)+".csproj");
        Information("Running tests from " + projectName);
        XDocument xdoc;
        using (var s = File.OpenRead(projectName))
            xdoc = XDocument.Load(s);

        List<string> frameworks = null;
        var targets = xdoc.Root.Descendants("TargetFrameworks").FirstOrDefault();
        if (targets != null)
            frameworks = targets.Value.Split(';').Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        else
            frameworks = new List<string> {xdoc.Root.Descendants("TargetFramework").First().Value};

        foreach(var fw in frameworks)
        {
            if (fw.StartsWith("net4")
                && RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                && Environment.GetEnvironmentVariable("FORCE_LINUX_TESTS") != "1")
            {
                Information($"Skipping {projectName} ({fw}) tests on Linux - https://github.com/mono/mono/issues/13969");
                continue;
            }

            Information($"Running for {projectName} ({fw}) ...");

            DotNetTest(c => c
                .SetProjectFile(projectName)
                .SetConfiguration(Parameters.Configuration)
                .SetFramework(fw)
                .EnableNoBuild()
                .EnableNoRestore()
                .When(Parameters.PublishTestResults, _ => _
                    .SetLogger("trx")
                    .SetResultsDirectory(Parameters.TestResultsRoot)));
        }
    }

    Target RunHtmlPreviewerTests => _ => _
        .DependsOn(CompileHtmlPreviewer)
        .OnlyWhenStatic(() => !(Parameters.SkipPreviewer || Parameters.SkipTests))
        .Executes(() =>
        {
            var webappTestDir = RootDirectory / "tests" / "Avalonia.DesignerSupport.Tests" / "Remote" / "HtmlTransport" / "webapp";

            NpmTasks.NpmInstall(c => c
                .SetProcessWorkingDirectory(webappTestDir)
                .SetProcessArgumentConfigurator(a => a.Add("--silent")));
            NpmTasks.NpmRun(c => c
                .SetProcessWorkingDirectory(webappTestDir)
                .SetCommand("test"));
        });

    Target RunCoreLibsTests => _ => _
        .OnlyWhenStatic(() => !Parameters.SkipTests)
        .DependsOn(Compile)
        .Executes(() =>
        {
            RunCoreTest("Avalonia.Animation.UnitTests");
            RunCoreTest("Avalonia.Base.UnitTests");
            RunCoreTest("Avalonia.Controls.UnitTests");
            RunCoreTest("Avalonia.Controls.DataGrid.UnitTests");
            RunCoreTest("Avalonia.Input.UnitTests");
            RunCoreTest("Avalonia.Interactivity.UnitTests");
            RunCoreTest("Avalonia.Layout.UnitTests");
            RunCoreTest("Avalonia.Markup.UnitTests");
            RunCoreTest("Avalonia.Markup.Xaml.UnitTests");
            RunCoreTest("Avalonia.Styling.UnitTests");
            RunCoreTest("Avalonia.Visuals.UnitTests");
            RunCoreTest("Avalonia.Skia.UnitTests");
            RunCoreTest("Avalonia.ReactiveUI.UnitTests");
        });

    Target RunRenderTests => _ => _
        .OnlyWhenStatic(() => !Parameters.SkipTests)
        .DependsOn(Compile)
        .Executes(() =>
        {
            RunCoreTest("Avalonia.Skia.RenderTests");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Nuke.Common.CI.TeamCity.TeamCity.Instance == null)// no direct2d tests on teamcity - they fail?
                RunCoreTest("Avalonia.Direct2D1.RenderTests");
        });

    Target RunDesignerTests => _ => _
        .OnlyWhenStatic(() => !Parameters.SkipTests && Parameters.IsRunningOnWindows)
        .DependsOn(Compile)
        .Executes(() =>
        {
            RunCoreTest("Avalonia.DesignerSupport.Tests");
        });

    [PackageExecutable("JetBrains.dotMemoryUnit", "dotMemoryUnit.exe")] readonly Tool DotMemoryUnit;

    Target RunLeakTests => _ => _
        .OnlyWhenStatic(() => !Parameters.SkipTests && Parameters.IsRunningOnWindows)
        .DependsOn(Compile)
        .Executes(() =>
        {
            var testAssembly = "tests\\Avalonia.LeakTests\\bin\\Release\\net461\\Avalonia.LeakTests.dll";
            DotMemoryUnit(
                $"{XunitPath.DoubleQuoteIfNeeded()} --propagate-exit-code -- {testAssembly}",
                timeout: 120_000);
        });

    Target ZipFiles => _ => _
        .After(CreateNugetPackages, Compile, RunCoreLibsTests, Package)
        .Executes(() =>
        {
            var data = Parameters;
            var pathToProjectSource = RootDirectory / "samples" / "ControlCatalog.NetCore";
            var pathToPublish = pathToProjectSource / "bin" / data.Configuration / "publish";

            DotNetPublish(c => c
                .SetProject(pathToProjectSource / "ControlCatalog.NetCore.csproj")
                .EnableNoBuild()
                .SetConfiguration(data.Configuration)
                .AddProperty("PackageVersion", data.Version)
                .AddProperty("PublishDir", pathToPublish));

            Zip(data.ZipCoreArtifacts, data.BinRoot);
            Zip(data.ZipNuGetArtifacts, data.NugetRoot);
            Zip(data.ZipTargetControlCatalogNetCoreDir, pathToPublish);
        });

    Target UpdateTeamCityVersion => _ => _
        .Executes(() =>
        {
            Nuke.Common.CI.TeamCity.TeamCity.Instance?.SetBuildNumber(Parameters.Version);
        });

    Target DownloadAvaloniaNativeLib => _ => _
        .After(Clean)
        .OnlyWhenStatic(() => EnvironmentInfo.IsWin)
        .Executes(() =>
        {
            //download avalonia native osx binary, so we don't have to build it on osx
            //expected to be -> Build/Products/Release/libAvalonia.Native.OSX.dylib
            //Avalonia.Native.0.10.0-preview5.nupkg
            string nugetversion = "0.10.14";

            var nugetdir = RootDirectory + "/Build/Products/Release/";
            //string nugeturl = "https://www.myget.org/F/avalonia-ci/api/v2/package/Avalonia.Native/";
            string nugeturl = "https://www.nuget.org/api/v2/package/Avalonia.Native/";

            nugeturl += nugetversion;

            //myget packages are expiring so i've made a copy here
            //google drive file share https://drive.google.com/open?id=1HK-XfBZRunGpxXcGUUEC-64H9T_n9cIJ
            //nugeturl = "https://drive.google.com/uc?id=1HK-XfBZRunGpxXcGUUEC-64H9T_n9cIJ&export=download";//Avalonia.Native.0.9.999-cibuild0005383-beta
            //nugeturl = "https://drive.google.com/uc?id=1fNKJ-KNsPtoi_MYVJZ0l4hbgHAkLMYZZ&export=download";//Avalonia.Native.0.9.2.16.nupkg custom build
            //nugeturl = "https://drive.google.com/uc?id=13ek3xvXA__GUgQFeAkemqE0lxiXiTr5s&export=download";//Avalonia.Native.0.10.0.8.nupkg custom build
            //nugeturl = "https://drive.google.com/uc?id=13n_Ql64s7eXncUQx_FagU4z5X-tBhtxC&export=download";//Avalonia.Native.0.10.0.16-rc1.nupkg custom build

            string nugetname = $"Avalonia.Native.{nugetversion}";
            string nugetcontentsdir = Path.Combine(nugetdir, nugetname);
            string nugetpath = nugetcontentsdir + ".nupkg";
            Logger.Info($"Downloading {nugetname} from {nugeturl}");
            Nuke.Common.IO.HttpTasks.HttpDownloadFile(nugeturl, nugetpath);
            System.IO.Compression.ZipFile.ExtractToDirectory(nugetpath, nugetcontentsdir, true);

            CopyFile(nugetcontentsdir + @"/runtimes/osx/native/libAvaloniaNative.dylib", nugetdir + "libAvalonia.Native.OSX." +
                "dylib", Nuke.Common.IO.FileExistsPolicy.Overwrite);
        });

    Target CreateIntermediateNugetPackages => _ => _
        .DependsOn(Compile)
        .After(RunTests)
        .Executes(() =>
        {
            if (Parameters.IsRunningOnWindows && !IsDotnetCoreOnlyBuild())

                MsBuildCommon(Parameters.MSBuildSolution, c => c
                    .AddProperty("PackAvaloniaNative", "true")
                    .AddTargets("Pack"));
            else
                DotNetPack(c => c
                    .SetProject(Parameters.MSBuildSolution)
                    .SetConfiguration(Parameters.Configuration)
                    .AddProperty("PackAvaloniaNative", "true")
                    .AddProperty("PackageVersion", Parameters.Version));
        });

    Target CreateNugetPackages => _ => _
        .DependsOn(CreateIntermediateNugetPackages)
        .Executes(() =>
        {
            BuildTasksPatcher.PatchBuildTasksInPackage(Parameters.NugetIntermediateRoot / "Avalonia.Build.Tasks." +
                                                       Parameters.Version + ".nupkg");
            var config = Numerge.MergeConfiguration.LoadFile(RootDirectory / "nukebuild" / "numerge.config");
            EnsureCleanDirectory(Parameters.NugetRoot);
            if(!Numerge.NugetPackageMerger.Merge(Parameters.NugetIntermediateRoot, Parameters.NugetRoot, config,
                new NumergeNukeLogger()))
                throw new Exception("Package merge failed");
        });

    private static string GetNuGetNugetPackagesDir()
    {
        string env(string v) => Environment.GetEnvironmentVariable(v);
        return env("NUGET_PACKAGES") ?? Path.Combine(env("USERPROFILE") ?? env("HOME"), ".nuget/packages");
    }

    Target PublishLocalNugetPackages => _ => _
    .Executes(() =>
    {
        string nugetPackagesDir = GetNuGetNugetPackagesDir();

        //clean up often locked dlls from avalonia packages
        var preCleanUpDirs = new[]
                {
                    "Avalonia/{0}/tools/", //Avalonia.Build.Tasks.dll
                    "Avalonia/{0}/lib/",
                    "Avalonia.Remote.Protocol/{0}/lib/" //Avalonia.Remote.Protocol.dll
                };
        foreach (var pattern in preCleanUpDirs)
        {
            var path = Path.Combine(nugetPackagesDir, string.Format(pattern, Parameters.Version));
            foreach (var filePath in Directory.GetFiles(path, "*.*", SearchOption.AllDirectories))
            {
                try
                {
                    DeleteFile(filePath);
                }
                catch (Exception e)
                {
                    Logger.Warn($"Will rename! Failed delete {e.Message} for {filePath}");
                    if (!filePath.EndsWith(".old"))
                        RenameFile(filePath, $"{filePath}.{Guid.NewGuid()}.old", Nuke.Common.IO.FileExistsPolicy.Overwrite);
                }
            }
        }

        foreach (var package in Directory.EnumerateFiles(Parameters.NugetRoot))
        {
            var packName = Path.GetFileName(package);
            string packgageFolderName = packName.Replace($".{Parameters.Version}.nupkg", "");
            var nugetCaheFolder = Path.Combine(nugetPackagesDir, packgageFolderName, Parameters.Version);

            //clean directory is not good, nuget will noticed and clean our files
            //EnsureCleanDirectory(nugetCaheFolder);
            EnsureExistingDirectory(nugetCaheFolder);

            CopyFile(package, nugetCaheFolder + "/" + packName, Nuke.Common.IO.FileExistsPolicy.Skip);

            Logger.Info($"Extracting to {nugetCaheFolder}, {package}");

            ZipFile.ExtractToDirectory(package, nugetCaheFolder, true);
        }
    });

    Target ClearLocalNugetPackages => _ => _
    .Executes(() =>
    {
        string nugetPackagesDir = GetNuGetNugetPackagesDir();

        foreach (var package in Directory.EnumerateFiles(Parameters.NugetRoot))
        {
            var packName = Path.GetFileName(package);
            string packgageFolderName = packName.Replace($".{Parameters.Version}.nupkg", "");
            var nugetCaheFolder = Path.Combine(nugetPackagesDir, packgageFolderName, Parameters.Version);

            EnsureCleanDirectory(nugetCaheFolder);
        }
    });

    Target RunTests => _ => _
        .DependsOn(RunCoreLibsTests)
        .DependsOn(RunRenderTests)
        .DependsOn(RunDesignerTests)
        .DependsOn(RunHtmlPreviewerTests)
        .DependsOn(RunLeakTests);

    Target Package => _ => _
        .DependsOn(RunTests)
        .DependsOn(CreateNugetPackages);

    Target CiAzureLinux => _ => _
        .DependsOn(RunTests);

    Target CiAzureOSX => _ => _
        .DependsOn(Package)
        .DependsOn(ZipFiles);

    Target CiAzureWindows => _ => _
        .DependsOn(Package)
        .DependsOn(ZipFiles);


    public static int Main() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Execute<Build>(x => x.Package)
            : Execute<Build>(x => x.RunTests);

}

public static class ToolSettingsExtensions
{
    public static T Apply<T>(this T settings, Configure<T> configurator)
    {
        return configurator != null ? configurator(settings) : settings;
    }
}
