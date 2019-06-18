#load build/paths.cake
#load build/version.cake
#load build/package.cake
#load build/urls.cake

#tool nuget:?package=GitVersion.CommandLine&version=4.0.0-beta0012
#tool nuget:?package=OctopusTools&version=6.7.0
#tool nuget:?package=Cake.Curl&version=4.1.0

#addin nuget:?package=Cake.Npm&version=0.17.0
#addin nuget:?package=Cake.Curl&version=4.1.0


var task = Argument("Target", "Compile");

Setup<PackageMetadata>(context => {
    return new PackageMetadata(
        outputDirectory:Argument("packageOutputDirectory", "packages"),
        name:"Linker-11");
});

Task("Compile")
    .Does(() =>
{
    DotNetCoreBuild(Paths.SolutionFile.FullPath);
});

Task("Test")
.IsDependentOn("Compile")
.Does(() => 
    {
        DotNetCoreTest(
            Paths.SolutionFile.FullPath,
            new DotNetCoreTestSettings{
                Logger = "trx",
                ResultsDirectory = Paths.TestResultDirectory
            });
    }
);

Task("Version")
    .Does<PackageMetadata>(package =>
{
    package.Version = ReadVersionFromProjectFile(Context);
    if (package.Version == null)
        package.Version = GitVersion().FullSemVer;

    Information($"Calculated version number {package.Version}");
});

Task("Build-Frontend")
    .Does(() =>
{
    NpmInstall(settings=> settings.FromPath(Paths.FrontendDirectory));
    NpmRunScript("build", settings => settings.FromPath(Paths.FrontendDirectory));
});

Task("Package-Zip")
    .IsDependentOn("Test")
    .IsDependentOn("Build-Frontend")
    .IsDependentOn("Version")
    .Does<PackageMetadata>(package => 
    {
        CleanDirectory(package.OutputDirectory); // Clean out the output directory to avoid it being full of old files
        package.Extension = "zip";
        DotNetCorePublish(
            Paths.WebProjectFile.GetDirectory().FullPath,
            new DotNetCorePublishSettings
            {
                OutputDirectory = Paths.PublishDirectory,
                NoBuild = true,
                NoRestore = true,
                MSBuildSettings = new DotNetCoreMSBuildSettings
                {
                    NoLogo = true
                }
            }
        );
        Zip(Paths.PublishDirectory, package.FullPath);
    });

Task("Package-Octopus")
    .IsDependentOn("Test")
    .IsDependentOn("Build-Frontend")
    .IsDependentOn("Version")
    .Does<PackageMetadata>(package => 
    {
        CleanDirectory(package.OutputDirectory); // Clean out the output directory to avoid it being full of old files
        package.Extension = "nupkg";
        DotNetCorePublish(
            Paths.WebProjectFile.GetDirectory().FullPath,
            new DotNetCorePublishSettings
            {
                OutputDirectory = Paths.PublishDirectory,
                NoBuild = true,
                NoRestore = true,
                MSBuildSettings = new DotNetCoreMSBuildSettings
                {
                    NoLogo = true
                }
            }
        );
        OctoPack(
            package.Name, 
            new OctopusPackSettings{
                Format = OctopusPackFormat.NuPkg,
                Version = package.Version,
                BasePath = Paths.PublishDirectory,
                OutFolder = package.OutputDirectory
            });
    });

Task("Deploy-Kudu")
    .Description("Deploys to Kudu using the zip deployment feature")
    .IsDependentOn("Package-Zip")
    .Does<PackageMetadata>(package => 
{
    CurlUploadFile(
        package.FullPath,
        Urls.KuduDeployUri,
        new CurlSettings
        {
            Username = EnvironmentVariable("DeploymentUser"),
            Password = EnvironmentVariable("DeploymentPassword"),
            RequestCommand = "POST",
            ArgumentCustomization = args => args.Append("--fail")
        });
});


// https://lnker.net/hj5v4uck <-- http://octopus-megakemp.northeurope.cloudapp.azure.com/app

Task("Deploy-Octopus")
    .IsDependentOn("Package-Octopus")
    .Does<PackageMetadata>(package => 
{
    OctoPush(
        Urls.OctopusServerUrl.AbsoluteUri,
        EnvironmentVariable("OctopusApiKey"),
        package.FullPath,
        new OctopusPushSettings
        {
            EnableServiceMessages = true,
            // ReplaceExisting = true  // <- Development only! Means we deploy package with different contents, but same version nr.
        }
    );

    OctoCreateRelease(
        "Linker-11",
        new CreateReleaseSettings{
            Server = Urls.OctopusServerUrl.AbsoluteUri,
            ApiKey = EnvironmentVariable("OctopusApiKey"),
            ReleaseNumber = package.Version,
            DefaultPackageVersion = package.Version,
            DeployTo = "Test",
            IgnoreExisting = true,
            DeploymentProgress = true,
            WaitForDeployment = true // Prosessen er pr default asynkron og vil lykkes uansett om ikke denne er satt
        });
});

Task("Set-Build-Number")
    .WithCriteria(() => BuildSystem.IsRunningOnAzurePipelinesHosted || BuildSystem.IsRunningOnAzurePipelines)
    .Does<PackageMetadata>(package => 
{
    var buildNumber = TFBuild.Environment.Build.Number;
    TFBuild.Commands.UpdateBuildNumber($"{package.Version}+{buildNumber}");
});

Task("Publish-Build-Artifact")
    .WithCriteria(() => BuildSystem.IsRunningOnAzurePipelinesHosted || BuildSystem.IsRunningOnAzurePipelines)
    .IsDependentOn("Package-Zip")
    .Does<PackageMetadata>(package => TFBuild.Commands.UploadArtifactDirectory(package.OutputDirectory));

Task("Publish-Test-Results")
    .WithCriteria(() => BuildSystem.IsRunningOnAzurePipelinesHosted || BuildSystem.IsRunningOnAzurePipelines)
    .IsDependentOn("Test")
    .Does(() => {
        var testResultData = new TFBuildPublishTestResultsData{
            TestRunner = TFTestRunnerType.VSTest,
            TestResultsFiles = GetFiles(Paths.TestResultDirectory + "/*.trx").ToList()
        };
        TFBuild.Commands.PublishTestResults(testResultData);
    });

Task("Build-CI")
    .IsDependentOn("Compile")
    .IsDependentOn("Test")
    .IsDependentOn("Build-FrontEnd")
    .IsDependentOn("Version")
    .IsDependentOn("Package-Zip")
    .IsDependentOn("Set-Build-Number")
    .IsDependentOn("Publish-Build-Artifact")
    .IsDependentOn("Publish-Test-Results")
    ;

RunTarget(task);