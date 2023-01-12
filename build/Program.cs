using Cake.Common;
using Cake.Common.Diagnostics;
using Cake.Common.IO;
using Cake.Common.Net;
using Cake.Common.Tools.NuGet;
using Cake.Common.Tools.NuGet.Restore;
using Cake.Core;
using Cake.Core.Diagnostics;
using Cake.Core.IO;
using Cake.Frosting;

using Octokit;

using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

public static class Program
{
	public static int Main(string[] args)
		=> new CakeHost()
			.UseContext<BuildContext>()
			.UseLifetime<Lifetime>()
			.UseWorkingDirectory("..")
			.Run(args);
}

public sealed class Solution
{
	public FilePath Path { get; set; }

	public DirectoryPath[] BinPaths { get; set; }

	public DirectoryPath[] ObjPaths { get; set; }
}

public sealed class Project
{
	public FilePath Path { get; set; }
}

public sealed class BuildContext : FrostingContext
{
	public Solution Solution { get; } = new();

	public Project Project { get; } = new();

	public string MsBuildConfig { get; set; }

	public DirectoryPath ToolsPath { get; set; }

	public FilePath MsBuildPath { get; set; }

	public FilePath NuGetPath { get; set; }

	public FilePath VsWherePath { get; set; }

	public FilePath GitVersionPath { get; set; }

	public bool UseMsBuildForNuGet { get; set; }

	public bool IsReleaseMsBuildConfig => MsBuildConfig is "Release";

	public bool IsSetMsBuildPath => IsExistFile(MsBuildPath.FullPath);

	public bool IsSetNuGetPath => IsExistFile(NuGetPath.FullPath);

	public bool IsSetVsWherePath => IsExistFile(VsWherePath.FullPath);

	public BuildContext(ICakeContext context)
		: base(context)
	{
		#region Arguments

		MsBuildConfig = context.Argument("config", "Release");
		MsBuildPath = FilePath.FromString(context.Argument("msbuildpath", string.Empty));
		NuGetPath = FilePath.FromString(context.Argument("nugetpath", string.Empty));
		VsWherePath = FilePath.FromString(context.Argument("vswherepath", string.Empty));
		UseMsBuildForNuGet = context.Argument("usemsbuildfornuget", false);

		#endregion

		ToolsPath = DirectoryPath.FromString($@"{context.Environment.WorkingDirectory}\tools");

		Solution.Path = context.GetFiles("./*.sln").First();
		Solution.BinPaths = NormalizeBinPaths(context.GetDirectories("./**/bin"));
		Solution.ObjPaths = NormalizeObjPaths(context.GetDirectories("./**/obj"));

		Project.Path = context.GetFiles("./**/*Test.csproj").First();
	}

	public bool IsExistFile(FilePath filePath)
		=> filePath is not null && File.Exists(filePath.FullPath);


	private DirectoryPath[] NormalizeBinPaths(DirectoryPathCollection paths)
		=> paths.Where(it => !it.FullPath.Contains("build") && !it.FullPath.Contains("obj")).ToArray();

	private DirectoryPath[] NormalizeObjPaths(DirectoryPathCollection paths)
		=> paths.Where(it => !it.FullPath.Contains("build")).ToArray();
}

public sealed class Lifetime : FrostingLifetime<BuildContext>
{
	public override void Setup(BuildContext context, ISetupContext info)
	{
		EnsureDirectoriesExists(context);
		DownloadTools(context);

		if (context.Environment.Platform.IsWindows())
			AutoDetectLastVsMsBuild(context);

		ReginsterTools(context);
	}

	public override void Teardown(BuildContext context, ITeardownContext info)
	{

	}

	public void EnsureDirectoriesExists(BuildContext context)
	{
		context.EnsureDirectoryExists(context.ToolsPath);
	}

	public void DownloadTools(BuildContext context)
	{
		DownloadNuGet(context);

		if (context.Environment.Platform.IsWindows())
			DownloadVsWhere(context);
	}

	public void DownloadNuGet(BuildContext context)
	{
		if (context.IsSetNuGetPath)
			return;

		var directoryName = "NuGet";
		var fileName = "nuget.exe";
		var filePath = context.Tools.Resolve(fileName);
		var isExistFile = context.IsExistFile(filePath);

		if (!isExistFile)
		{
			var fullDirectoryName = $@"{context.ToolsPath}/{directoryName}";
			var fullFilePath = FilePath.FromString($@"{fullDirectoryName}/{fileName}");

			context.EnsureDirectoryExists(fullDirectoryName);
			context.DownloadFile(
				$"https://dist.nuget.org/win-x86-commandline/latest/{fileName}",
				fullFilePath);

			filePath = fullFilePath;
		}

		context.NuGetPath = filePath;
	}

	public void DownloadVsWhere(BuildContext context)
	{
		// https://blogs.msdn.microsoft.com/heaths/2017/04/21/vswhere-is-now-installed-with-visual-studio-2017/

		if (context.IsSetVsWherePath)
			return;

		var directoryName = "VsWhere";
		var fileName = "vswhere.exe";
		var filePath = context.Tools.Resolve(fileName);
		var isExistFile = context.IsExistFile(filePath);
		var github = new GitHubClient(new ProductHeaderValue("vswhere"));
		var latestRelease = github.Repository.Release.GetLatest("microsoft", "vswhere").GetAwaiter().GetResult();

		if (!isExistFile)
		{
			var fullDirectoryName = $@"{context.ToolsPath}/{directoryName}";
			var fullFilePath = FilePath.FromString($@"{fullDirectoryName}/{fileName}");

			context.EnsureDirectoryExists(fullDirectoryName);
			context.DownloadFile(latestRelease.Assets[0].BrowserDownloadUrl, fullFilePath);

			filePath = fullFilePath;
		}

		context.VsWherePath = filePath;
	}

	public void AutoDetectLastVsMsBuild(BuildContext context)
	{
		// context.VSWhereLatest();

		if (context.IsSetMsBuildPath)
			return;

		var exitCode = context.StartProcess(context.VsWherePath, new ProcessSettings()
			.WithArguments(it =>
			{
				it.Append("-latest");
				it.Append("-format");
				it.Append("json");
				it.Append("-requires");
				it.Append("Microsoft.Component.MSBuild");
			})
			.SetRedirectStandardOutput(true), out var list);

		if (exitCode != 0)
			return;

		var jNode = JsonNode.Parse(string.Join(string.Empty, list.ToArray()));
		var vsInstallationPath = jNode[0]["installationPath"];
		var filePath = FilePath.FromString($"{vsInstallationPath}/Msbuild/Current/Bin/MSBuild.exe");

		context.MsBuildPath = filePath;
	}

	public void ReginsterTools(BuildContext context)
	{
		context.Tools.RegisterFile(context.MsBuildPath);
		context.Tools.RegisterFile(context.NuGetPath);
		context.Tools.RegisterFile(context.VsWherePath);
	}
}

#region Tasks

[IsDependentOn(typeof(NuGetRestore))]
public sealed class Default : FrostingTask
{

}

[TaskName("Clean")]
public sealed class Clean : FrostingTask<BuildContext>
{
	public override void Run(BuildContext context)
	{
		context.Information("Starting deleting a Bin folders...");
		context.CleanDirectories(context.Solution.BinPaths);
		context.Information("Ended deleting a Bin folders.");

		context.Information(string.Empty);

		context.Information("Starting deleting an Obj folders...");
		context.CleanDirectories(context.Solution.ObjPaths);
		context.Information("Ended deleting an Obj folders.");
	}
}

[TaskName("NuGetRestore")]
[IsDependentOn(typeof(Clean))]
public sealed class NuGetRestore : FrostingTask<BuildContext>
{
	public override void Run(BuildContext context)
	{
		if (context.UseMsBuildForNuGet && context.IsSetMsBuildPath)
		{
			context.NuGetRestore(context.Solution.Path,
				new NuGetRestoreSettings
				{
					MSBuildPath = context.MsBuildPath.GetDirectory()
				});
		}
		else
			context.NuGetRestore(context.Solution.Path);
	}
}

#endregion