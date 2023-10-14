using Cross;
using Docker.DotNet;
using Docker.DotNet.Models;
using System.Diagnostics;
using System.Formats.Tar;
using System.Text.Json;
using System.Text.RegularExpressions;
using static System.Console;
using static Program;

var cancellationTokenSource = new CancellationTokenSource();
var cancellationToken = cancellationTokenSource.Token;
CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationTokenSource.Cancel();
};

var (rid, os, isInvalidOptions, isLinux, isMusl) = Options.Create(args);

if (isInvalidOptions)
    return Die("Invalid options.");
if (rid is null && os is null)
{
    WriteLine("Running dotnet directly.");
    return await RunDotnetAsync(args, cancellationToken);
}
var currentRID = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
if (Environment.Version.Major >= 8 && (currentRID == rid || IsItMusl(rid, os) == IsItMusl(currentRID, null)))
{
    WriteLine("No need to run in container, RID/OS matches, running dotnet directly.");
    return await RunDotnetAsync(args, cancellationToken);
}
if (!isLinux)
    return Die("Only Build for Linux is supported.");
const string sdkVersion = "8.0";
var imageMusl = new Image("musl", $"mcr.microsoft.com/dotnet/sdk:{sdkVersion}-alpine", "apk add clang build-base zlib-dev docker-cli");
var imageGlibc = new Image("glibc", $"mcr.microsoft.com/dotnet/sdk:{sdkVersion}-jammy",
        "apt-get update && apt-get install -y clang zlib1g-dev curl gnupg",
        "curl -fsSL https://download.docker.com/linux/ubuntu/gpg | gpg --dearmor -o /etc/apt/keyrings/docker.gpg && chmod a+r /etc/apt/keyrings/docker.gpg",
        """echo "deb [arch="$(dpkg --print-architecture)" signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu "$(. /etc/os-release && echo "$VERSION_CODENAME")" stable" > /etc/apt/sources.list.d/docker.list""",
        "apt-get update && apt-get install -y docker-ce-cli");
var image = isMusl ? imageMusl : imageGlibc;
var imageTag = $"dotnet-build:{sdkVersion}-{image.LibC}";
using var client = new DockerClientConfiguration().CreateClient();
try
{
    if (await ImageDoesNotExistAsync())
    {
        WriteLine("Building Docker image...");
        if (!await BuildImageAsync())
            return Die();
    }
    else
    {
        WriteLine("Docker image already exists.");
    }
    WriteLine("Running dotnet through container.");
    var containerName = await CreateContainerNameAsync();
    var containerId = await RunContainerAsync(containerName);
    if (containerId is null)
        return Die();
    await client.Containers.RemoveContainerAsync(containerId, new() { Force = true }, cancellationToken);
    WriteLine("Done.");
}
catch (OperationCanceledException)
{
    return Die();
}
catch (Exception ex)
{
    return Die($"Got unexpected error: {ex.Message}.");
}
return 0;

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
            return Die();
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
#pragma warning disable CA1869
    var jsonSerializerOptions = new JsonSerializerOptions() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
#pragma warning restore CA1869
    var buildCommands = image.BuildCommands.Aggregate("", (acc, next) => $"{acc}RUN {next}\n");
    var dockerfile = $""""
FROM {image.Tag}
WORKDIR /app
ENV HOME=/home/user
{buildCommands}
RUN mkdir /home/user
{(OperatingSystem.IsLinux() ? $"RUN chown {Linux.getuid()}:{Linux.getgid()} /home/user" : "# chown only for Linux")}
"""";
    const string dockerfileName = "Dockerfile";
    var dockerfileDirectory = Path.Join(Path.GetTempPath(), $"dotnet-build_{sdkVersion}-{image.LibC}");
    var dockerfilePath = Path.Join(dockerfileDirectory, dockerfileName);
    try
    {
        if (Directory.Exists(dockerfileDirectory))
            Directory.Delete(dockerfileDirectory, true);
        Directory.CreateDirectory(dockerfileDirectory);
        File.WriteAllText(dockerfilePath, dockerfile);
        var succeeded = true;
        var buildProgress = new Progress<JSONMessage>(log =>
        {
            if (log.Stream != null)
                Write(log.Stream);
            if (log.ID != null)
            {
                if (log.Status != null)
                    WriteLine($"{log.ID}: {log.Status}");
                else
                    WriteLine(log.ID);
            }
            else if (log.Status != null)
            {
                WriteLine(log.Status);
            }
            if (log.Progress != null)
                WriteLine(log.Progress);
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
    catch (DockerApiException ex)
    {
        var message = JsonSerializer.Deserialize<JSONError>(ex.ResponseBody, jsonSerializerOptions)?.Message;
        Die("Error building image:", message is not null ? message : ex.ResponseBody);
        return false;
    }
    catch (TaskCanceledException)
    {
        return false;
    }
    catch (Exception ex)
    {
        Error.WriteLine("Got unexpected error:");
        Die(ex.ToString());
        return false;
    }
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
        .Append(0)
        .Max();
    var containerName = $"dotnet-build-{++buildContainersRunning:0000}";
    return containerName;
}

async Task<string?> RunContainerAsync(string containerName)
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
                // todo: should we ever do this? It fails on Windows, for me: OperatingSystem.IsWindows() ? "//./pipe/docker_engine://./pipe/docker_engine" : "/var/run/docker.sock:/var/run/docker.sock",
                "/var/run/docker.sock:/var/run/docker.sock",
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


public partial class Program
{
    [GeneratedRegex("^/dotnet-build-(\\d{4})$")]
    private static partial Regex DotnetBuildContainer();

    public static bool IsItMusl(string? rid, string? os) => (rid != null && rid.StartsWith("linux-musl-")) || os == "linux-musl";
}

internal record struct Image(string LibC, string Tag, params string[] BuildCommands);

internal record struct Options(string? RID, string? OS, bool InvalidOptions, bool IsLinux, bool IsMusl)
{
    public static Options Create(string[] args)
    {
        var validOptions = TryGetRid(args, out var rid)
            & TryGetOS(args, out var os);
        var isLinux = (rid != null && (rid == "linux" || rid.StartsWith("linux-"))) || (os is not null && (os == "linux" || os.StartsWith("linux-")));
        // we have 2 big Linux families: glibc and musl (in .NET they are 2 OSs: "linux" and "linux-musl")
        var isMusl = isLinux && IsItMusl(rid, os);
        return new(rid, os, !validOptions, isLinux, isMusl);
    }

    private static bool TryGetRid(string[] args, out string? rid) => TryGetOption(args, out rid, "-r", "--runtime");

    private static bool TryGetOS(string[] args, out string? os) => TryGetOption(args, out os, null, "--os");

    private static bool TryGetOption(string[] args, out string? value, string? shortOption, string longOption)
    {
        var index = Array.IndexOf(args, shortOption);
        if (index == -1)
            index = Array.IndexOf(args, longOption);
        if (index > -1)
        {
            if (args.Length > index + 1)
            {
                var candidate = args[index + 1];
                if (candidate != null && !candidate.StartsWith('-'))
                {
                    value = candidate;
                }
                else
                {
                    value = null;
                    return false;
                }
            }
            else
            {
                value = null;
                return false;
            }
        }
        else
        {
            var candidate = args.FirstOrDefault(arg => arg.StartsWith($"{shortOption}=") || arg.StartsWith($"{longOption}="));
            value = candidate?[(candidate.IndexOf('=') + 1)..];
        }
        return true;
    }
}
