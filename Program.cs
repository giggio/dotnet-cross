using Cross;
using Docker.DotNet;
using Docker.DotNet.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Formats.Tar;
using System.Text.Json;
using System.Text.RegularExpressions;
using static System.Console;

var cancellationTokenSource = new CancellationTokenSource();
var cancellationToken = cancellationTokenSource.Token;
CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationTokenSource.Cancel();
};

if (!TryGetRid(args, out var rid))
    return Die("Invalid RID.");
if (rid == null)
{
    WriteLine("Running dotnet directly.");
    return await RunDotnetAsync(args, cancellationToken);
}

const string sdkVersion = "8.0";
ReadOnlyCollection<Image> imageList = new(
[
        new("linux-musl-x64", $"mcr.microsoft.com/dotnet/sdk:{sdkVersion}-alpine", "apk add clang build-base zlib-dev docker-cli")
    ]
);
var images = imageList.ToDictionary(i => i.RID, i => i);
if (!images.TryGetValue(rid, out var image))
    return Die($"Runtime identifier (RID) not supported: {rid}.");
using var client = new DockerClientConfiguration().CreateClient();
var imageTag = $"dotnet-build:{sdkVersion}-{image.RID}";
if (await ImageDoesNotExistAsync())
{

#pragma warning disable CA1869
    var jsonSerializerOptions = new JsonSerializerOptions() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
#pragma warning restore CA1869
    WriteLine("Building Docker image...");
    try
    {
        if (!await BuildImageAsync())
            return 1;
    }
    catch (DockerApiException ex)
    {
        var message = JsonSerializer.Deserialize<JSONError>(ex.ResponseBody, jsonSerializerOptions)?.Message;
        return Die("Error building image:", message is not null ? message : ex.ResponseBody);
    }
    catch (Exception ex)
    {
        Error.WriteLine("Got unexpected error:");
        return Die(ex.ToString());
    }
}
else
{
    WriteLine("Docker image already exists.");
}

WriteLine("Running dotnet through container.");
var containerName = await CreateContainerNameAsync();
var targetIsWindows = rid.StartsWith("win");
var containerId = await RunContainerAsync();
if (containerId is null)
    return 1;
await client.Containers.RemoveContainerAsync(containerId, new() { Force = true }, cancellationToken);
WriteLine("Done.");
return 0;

static bool TryGetRid(string[] args, out string? rid)
{
    var ridIndex = Array.IndexOf(args, "-r");
    if (ridIndex == -1)
        Array.IndexOf(args, "--runtime");
    if (ridIndex > -1)
    {
        if (args.Length > ridIndex + 1)
        {
            var ridCandidate = args[ridIndex + 1];
            if (ridCandidate != null && !ridCandidate.StartsWith('-'))
            {
                rid = ridCandidate;
            }
            else
            {
                rid = null;
                return false;
            }
        }
        else
        {
            rid = null;
            return false;
        }
    }
    else
    {
        var ridCandidate = args.FirstOrDefault(arg => arg.StartsWith("-r=") || arg.StartsWith("--runtime"));
        rid = ridCandidate ?? null;
    }
    return true;
}

async Task<int> RunDotnetAsync(string[] args, CancellationToken cancellationToken)
{
    var dotnet = "dotnet";
    if (OperatingSystem.IsWindows())
        dotnet += ".exe";
    var dotnetPath = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator).Select(path => Path.Combine(path, dotnet)).FirstOrDefault(File.Exists);
    var arguments = string.Join(" ", args.Select(arg => arg.Contains(' ') ? $"\"{arg}\"" : arg));
    var process = Process.Start(new ProcessStartInfo(dotnet, arguments)
    {
        WorkingDirectory = Environment.CurrentDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = false,
        RedirectStandardError = false,
        RedirectStandardInput = false
    });
    if (process != null)
    {
        await process.WaitForExitAsync(CancellationToken.None);
        if (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
                process.Kill(true);
            return 1;
        }
        return process.ExitCode;
    }
    else
    {
        return Die("Failed to start dotnet process.");
    }
}

async Task<bool> ImageDoesNotExistAsync()
{
    var imagesFound = await client.Images.ListImagesAsync(new()
    {
        All = true,
        Filters = new Dictionary<string, IDictionary<string, bool>>() { { "reference", new Dictionary<string, bool> { { imageTag, true } } } }
    }, cancellationToken);
    var noImageFound = imagesFound.Count == 0;
    return noImageFound;
}

async Task<bool> BuildImageAsync()
{
    var dockerfile = $""""
FROM {image.Tag}
WORKDIR /app
ENV HOME=/home/user
RUN {image.BuildCommands}
RUN mkdir /home/user
{(OperatingSystem.IsLinux() ? $"RUN chown {Linux.getuid()}:{Linux.getgid()} /home/user" : "# chown only for Linux")}
"""";

    var dockerfileName = $"Dockerfile.dotnet-build_{sdkVersion}-{image.RID}";
    var dockerfileDirectory = Path.Join(Path.GetTempPath(), "dotnet-build_{sdkVersion}-{image.RID}");
    var dockerfilePath = Path.Join(dockerfileDirectory, dockerfileName);
    if (Directory.Exists(dockerfileDirectory))
        Directory.Delete(dockerfileDirectory, true);
    Directory.CreateDirectory(dockerfileDirectory);
    File.WriteAllText(dockerfilePath, dockerfile);
    var succeeded = true;
    var buildProgress = new Progress<JSONMessage>(log =>
    {
        if (log.Stream != null)
            WriteLine(log.Stream);
        if (log.Error != null)
        {
            Error.WriteLine($"error code: {log.Error.Code}, error message: {log.Error.Message}");
            succeeded = false;
        }
    });
    using var archiveStream = new MemoryStream();
    TarFile.CreateFromDirectory(sourceDirectoryName: dockerfileDirectory, destination: archiveStream, includeBaseDirectory: false);
    archiveStream.Position = 0;
    await client.Images.BuildImageFromDockerfileAsync(new()
    {
        Tags = new List<string> { { imageTag } },
        Dockerfile = dockerfileName
    },
    archiveStream, null, null, buildProgress, cancellationToken);
    return succeeded;
}

async Task<string> CreateContainerNameAsync()
{
    var containersRunning = await client.Containers.ListContainersAsync(new() { All = true });
    var buildContainersRunning = containersRunning
        .Select(container => container.Names.FirstOrDefault())
        .Select(name =>
        {
            if (name is null)
                return 0;
            var match = DotnetBuildContainer().Match(name);
            return match.Success ? int.Parse(match.Groups[1].Value) : 0;
        })
        .Where(id => id > 0)
        .Max();
    var containerName = $"dotnet-build-{++buildContainersRunning:0000}";
    return containerName;
}

async Task<string?> RunContainerAsync()
{
    var createContainer = await client.Containers.CreateContainerAsync(new(new()
    {
        Image = imageTag,
        Cmd = ["dotnet", .. args],
        User = OperatingSystem.IsLinux() ? $"{Linux.getuid()}:{Linux.getgid()}" : null,
        Tty = true,
        AttachStderr = true,
        AttachStdin = false,
        AttachStdout = true
    })
    {
        Name = containerName,
        HostConfig = new()
        {
            Binds = [
                targetIsWindows ? "//./pipe/docker_engine://./pipe/docker_engine" : "/var/run/docker.sock:/var/run/docker.sock",
                $"{Environment.CurrentDirectory}:/app"
            ]
        }
    }, cancellationToken);
    var containerId = createContainer.ID;
    if (createContainer.Warnings.Any())
    {
        Error.WriteLine("Got error when creating container:");
        foreach (var warning in createContainer.Warnings)
            Error.WriteLine(warning);
        Error.WriteLine($"Removing container {containerName} ({containerId}).");
        await client.Containers.RemoveContainerAsync(containerId, new() { Force = true }, cancellationToken);
        return null;
    }
    var started = await client.Containers.StartContainerAsync(containerId, new(), cancellationToken);
    if (!started)
    {
        await client.Containers.RemoveContainerAsync(containerId, new() { Force = true }, cancellationToken);
        Error.WriteLine("Container did not start.");
        return null;
    }
    var stream = await client.Containers.GetContainerLogsAsync(containerId, true, new() { ShowStdout = true, ShowStderr = true, Follow = true }, cancellationToken);
    await stream.CopyOutputToAsync(OpenStandardInput(), OpenStandardOutput(), OpenStandardError(), cancellationToken);
    return containerId;
}

int Die(params string[] messages)
{
    cancellationTokenSource.Cancel();
    foreach (var message in messages)
        Error.WriteLine(message);
    return 1;
}

internal record struct Image(string RID, string Tag, string BuildCommands);

public partial class Program
{
    [GeneratedRegex("^/dotnet-build-(\\d{4})$")]
    private static partial Regex DotnetBuildContainer();
}