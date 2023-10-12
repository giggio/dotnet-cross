// CONTAINER_ID=$(docker run -d -v /var/run/docker.sock:/var/run/docker.sock -v "$DIR":/app mcr.microsoft.com/dotnet/sdk:8.0-alpine \
//   sh -c "mkdir /home/user && chown `id -u`:`id -g` /home/user && apk add clang build-base zlib-dev docker-cli && touch /.done && sleep infinity")
// while ! docker exec -ti $CONTAINER_ID ls /.done > /dev/null; do docker logs $CONTAINER_ID; sleep 2; done
// docker exec -ti --workdir /app --user `id -u`:`id -g` -e HOME=/home/user $CONTAINER_ID sh -c \
//   'dotnet publish -r linux-musl-x64 -t:PublishContainer -p:ContainerBaseImage=mcr.microsoft.com/dotnet/runtime-deps:8.0-alpine -p:ContainerImageTag=latest -p:PublishAot=true -p:DebugType=none -c Release'
// docker rm -f $CONTAINER_ID
using Docker.DotNet;
using Cross;
using static System.Console;
using System.Diagnostics;

var cancellationTokenSource = new CancellationTokenSource();
var cancellationToken = cancellationTokenSource.Token;
CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationTokenSource.Cancel();
};

if (!TryGetRid(args, out var rid))
{
    Error.WriteLine("Invalid RID.");
    return 1;
}
if (rid == null)
{
    // run with default command
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
        Error.WriteLine("Failed to start dotnet process.");
        return 1;
    }
}

List<Image> imageList = new()
{
    { new Image("linux-musl-x64", "mcr.microsoft.com/dotnet/sdk:8.0-alpine", "apk add clang build-base zlib-dev docker-cli") }
};
var images = imageList.ToDictionary(i => i.RID, i => i);

if (!images.TryGetValue(rid, out var image))
{
    Error.WriteLine($"Runtime identifier (RID) not supported: {rid}.");
    return 1;
}
var targetIsWindows = rid.StartsWith("win");
using var client = new DockerClientConfiguration().CreateClient();
var doneString = $"#done-{Guid.NewGuid()}#";
var chown = OperatingSystem.IsLinux() ? $"&& chown {Linux.getuid()}:{Linux.getgid} /home/user" : "";
var dockerSocket = targetIsWindows ? "//./pipe/docker_engine://./pipe/docker_engine" : "/var/run/docker.sock:/var/run/docker.sock";
var listResponse = await client.Containers.ListContainersAsync(new() { All = true });
var buildContainersRunning = listResponse.Count(r => r.Names.Any(name => name.StartsWith("/dotnet-build-")));
var containerName = $"dotnet-build-{++buildContainersRunning:0000}";
var dockerfile = $""""
FROM {image.Tag}
RUN {image.BuildCommands}
RUN mkdir /home/user
"""";
//client.Images.BuildImageFromDockerfileAsync()
var createContainer = await client.Containers.CreateContainerAsync(new(new()
{
    Image = image.Tag,
    Cmd = new List<string> { "sh", "-c", $"""mkdir /home/user {chown} && {image.BuildCommands} && touch /.done && echo '{doneString}' && sleep infinity""" }
})
{
    Name = containerName,
    HostConfig = new()
    {
        Binds = new List<string>
        {
            dockerSocket,
            $"{Environment.CurrentDirectory}:/app"
        }
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
    return 1;
}

var started = await client.Containers.StartContainerAsync(containerId, new(), cancellationToken);
if (!started)
{
    await client.Containers.RemoveContainerAsync(containerId, new() { Force = true }, cancellationToken);
    Error.WriteLine("Container did not start.");
    return 1;
}
CancellationTokenSource logsCancellationTokenSource = new();
var progress = new Progress<string>(log =>
{
    WriteLine(log);
    if (log.Contains(doneString, StringComparison.InvariantCulture))
        logsCancellationTokenSource.Cancel();
});

try
{
    await client.Containers.GetContainerLogsAsync(containerId, new() { ShowStdout = true, ShowStderr = true, Follow = true }, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, logsCancellationTokenSource.Token).Token, progress);
}
catch (TaskCanceledException) { }

// docker exec -ti --workdir /app --user `id -u`:`id -g` -e HOME=/home/user $CONTAINER_ID sh -c \
//   'dotnet publish -r linux-musl-x64 -t:PublishContainer -p:ContainerBaseImage=mcr.microsoft.com/dotnet/runtime-deps:8.0-alpine -p:ContainerImageTag=latest -p:PublishAot=true -p:DebugType=none -c Release'

var exec = await client.Exec.ExecCreateContainerAsync(containerId, new()
{
    WorkingDir = "/app",
    User = OperatingSystem.IsLinux() ? $"{Linux.getuid()}:{Linux.getgid}" : null,
    Env = new List<string> { "HOME=/home/user" },
    Cmd = ["dotnet", .. args],
    Tty = true,
    AttachStderr = true,
    AttachStdin = false,
    AttachStdout = true
}, cancellationToken);

using var stream = await client.Exec.StartAndAttachContainerExecAsync(exec.ID, true, cancellationToken);
await stream.CopyOutputToAsync(OpenStandardInput(), OpenStandardOutput(), OpenStandardError(), cancellationToken);
await client.Containers.RemoveContainerAsync(containerId, new() { Force = true }, cancellationToken);
WriteLine("Done");
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
        if (ridCandidate == null)
        {
            // run with default command
            rid = null;
        }
        else
        {
            // run with container
            rid = ridCandidate;
        }
    }
    return true;
}

internal record Image(string RID, string Tag, string BuildCommands);
