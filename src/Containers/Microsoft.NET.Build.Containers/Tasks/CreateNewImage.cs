﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Extensions.Logging;
using Microsoft.NET.Build.Containers.Logging;
using Microsoft.NET.Build.Containers.Resources;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Microsoft.NET.Build.Containers.Tasks;

public sealed partial class CreateNewImage : Microsoft.Build.Utilities.Task, ICancelableTask, IDisposable
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    /// <summary>
    /// Unused. For interface parity with the ToolTask implementation of the task.
    /// </summary>
    public string ToolExe { get; set; }

    /// <summary>
    /// Unused. For interface parity with the ToolTask implementation of the task.
    /// </summary>
    public string ToolPath { get; set; }

    private bool IsLocalPush => string.IsNullOrEmpty(OutputRegistry);

    private bool IsLocalPull => string.IsNullOrEmpty(BaseRegistry);

    public void Cancel() => _cancellationTokenSource.Cancel();

    public override bool Execute()
    {
        return Task.Run(() => ExecuteAsync(_cancellationTokenSource.Token)).GetAwaiter().GetResult();
    }

    internal async Task<bool> ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using MSBuildLoggerProvider loggerProvider = new(Log);
        ILoggerFactory msbuildLoggerFactory = new LoggerFactory(new[] { loggerProvider });
        ILogger logger = msbuildLoggerFactory.CreateLogger<CreateNewImage>();

        if (!Directory.Exists(PublishDirectory))
        {
            Log.LogErrorWithCodeFromResources(nameof(Strings.PublishDirectoryDoesntExist), nameof(PublishDirectory), PublishDirectory);
            return !Log.HasLoggedErrors;
        }

        Registry? sourceRegistry = IsLocalPull ? null : new Registry(BaseRegistry, logger);
        ImageReference sourceImageReference = new(sourceRegistry, BaseImageName, BaseImageTag);

        Registry? destinationRegistry = IsLocalPush ? null : new Registry(OutputRegistry, logger);
        IEnumerable<ImageReference> destinationImageReferences = ImageTags.Select(t => new ImageReference(destinationRegistry, Repository, t));

        ImageBuilder? imageBuilder;
        if (sourceRegistry is { } registry)
        {
            imageBuilder = await registry.GetImageManifestAsync(
                BaseImageName,
                BaseImageTag,
                ContainerRuntimeIdentifier,
                RuntimeIdentifierGraphPath,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new NotSupportedException(Resource.GetString(nameof(Strings.ImagePullNotSupported)));
        }

        if (imageBuilder is null)
        {
            Log.LogErrorWithCodeFromResources(nameof(Strings.BaseImageNotFound), sourceImageReference, ContainerRuntimeIdentifier);
            return !Log.HasLoggedErrors;
        }

        SafeLog("Building image '{0}' with tags {1} on top of base image {2}", Repository, String.Join(",", ImageTags), sourceImageReference);

        Layer newLayer = Layer.FromDirectory(PublishDirectory, WorkingDirectory, imageBuilder.IsWindows);
        imageBuilder.AddLayer(newLayer);
        imageBuilder.SetWorkingDirectory(WorkingDirectory);

        (string[] entrypoint, string[] cmd) = DetermineEntrypointAndCmd(baseImageEntrypoint: imageBuilder.BaseImageConfig.GetEntrypoint());
        imageBuilder.SetEntrypointAndCmd(entrypoint, cmd);

        foreach (ITaskItem label in Labels)
        {
            imageBuilder.AddLabel(label.ItemSpec, label.GetMetadata("Value"));
        }

        SetEnvironmentVariables(imageBuilder, ContainerEnvironmentVariables, logger);

        SetPorts(imageBuilder, ExposedPorts);

        if (ContainerUser is { Length: > 0 } user)
        {
            imageBuilder.SetUser(user);
        }

        // it's a common convention to apply custom users with the APP_UID convention - we check and apply that here
        if (imageBuilder.EnvironmentVariables.TryGetValue(KnownStrings.EnvironmentVariables.APP_UID, out string? appUid))
        {
            imageBuilder.SetUser(appUid);
        }

        // asp.net images control port bindings via three environment variables. we should check for those variables and ensure that ports are created for them
        if (imageBuilder.EnvironmentVariables.TryGetValue(KnownStrings.EnvironmentVariables.ASPNETCORE_HTTP_PORTS, out string? httpPorts))
        {
            logger.LogTrace("Setting ports from ASPNETCORE_HTTP_PORTS environment variable");
            foreach(var port in httpPorts.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(port, out int parsedPort))
                {
                    logger.LogTrace("Added port {port}", parsedPort);
                    imageBuilder.ExposePort(parsedPort, PortType.tcp);
                }
                else
                {
                    logger.LogTrace("Skipped port {port} because it could not be parsed as an integer", port);
                }
            }
        }
        if (imageBuilder.EnvironmentVariables.TryGetValue(KnownStrings.EnvironmentVariables.ASPNETCORE_HTTPS_PORTS, out string? httpsPorts))
        {
            logger.LogTrace("Setting ports from ASPNETCORE_HTTPS_PORTS environment variable");
            foreach(var port in httpsPorts.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(port, out int parsedPort))
                {
                    logger.LogTrace("Added port {port}", parsedPort);
                    imageBuilder.ExposePort(parsedPort, PortType.tcp);
                }
                else
                {
                    logger.LogTrace("Skipped port {port} because it could not be parsed as an integer", port);
                }
            }
        }
        if (imageBuilder.EnvironmentVariables.TryGetValue(KnownStrings.EnvironmentVariables.ASPNETCORE_URLS, out string? urls))
        {
            var aspnetPortRegex = new Regex(@"(?<scheme>\w+)://(?<domain>([*+]|).+):(?<port>\d+)");
            foreach(var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                logger.LogTrace("Setting ports from ASPNETCORE_HTTPS_PORTS environment variable");
                var match = aspnetPortRegex.Match(url);
                if (match.Success && int.TryParse(match.Groups["port"].Value, out int port))
                {
                    logger.LogTrace("Added port {port}", port);
                    imageBuilder.ExposePort(port, PortType.tcp);
                }
            }
        }

        // at the end of this step, if any failed then bail out.
        if (Log.HasLoggedErrors)
        {
            return false;
        }

        BuiltImage builtImage = imageBuilder.Build();
        cancellationToken.ThrowIfCancellationRequested();

        // at this point we're done with modifications and are just pushing the data other places
        GeneratedContainerManifest = JsonSerializer.Serialize(builtImage.Manifest);
        GeneratedContainerConfiguration = builtImage.Config;

        foreach (ImageReference destinationImageReference in destinationImageReferences)
        {
            if (IsLocalPush)
            {
                ILocalRegistry localRegistry = KnownLocalRegistryTypes.CreateLocalRegistry(LocalRegistry, msbuildLoggerFactory);
                if (!(await localRegistry.IsAvailableAsync(cancellationToken).ConfigureAwait(false)))
                {
                    Log.LogErrorWithCodeFromResources(nameof(Strings.LocalRegistryNotAvailable));
                    return false;
                }
                try
                {
                    await localRegistry.LoadAsync(builtImage, sourceImageReference, destinationImageReference, cancellationToken).ConfigureAwait(false);
                    SafeLog("Pushed image '{0}' to local registry", destinationImageReference.RepositoryAndTag);
                }
                catch (AggregateException ex) when (ex.InnerException is DockerLoadException dle)
                {
                    Log.LogErrorFromException(dle, showStackTrace: false);
                }
            }
            else
            {
                try
                {
                    if (destinationImageReference.Registry is not null)
                    {
                        await destinationImageReference.Registry.PushAsync(
                            builtImage,
                            sourceImageReference,
                            destinationImageReference,
                            cancellationToken).ConfigureAwait(false);
                        SafeLog("Pushed image '{0}' to registry '{1}'", destinationImageReference, OutputRegistry);
                    }
                }
                catch (ContainerHttpException e)
                {
                    if (BuildEngine != null)
                    {
                        Log.LogErrorFromException(e, true);
                    }
                }
                catch (Exception e)
                {
                    if (BuildEngine != null)
                    {
                        Log.LogErrorWithCodeFromResources(nameof(Strings.RegistryOutputPushFailed), e.Message);
                        Log.LogMessage(MessageImportance.Low, "Details: {0}", e);
                    }
                }
            }
        }

        return !Log.HasLoggedErrors;
    }

    private void SetPorts(ImageBuilder image, ITaskItem[] exposedPorts)
    {
        foreach (var port in exposedPorts)
        {
            var portNo = port.ItemSpec;
            var portType = port.GetMetadata("Type");
            if (ContainerHelpers.TryParsePort(portNo, portType, out Port? parsedPort, out ContainerHelpers.ParsePortError? errors))
            {
                image.ExposePort(parsedPort.Value.Number, parsedPort.Value.Type);
            }
            else
            {
                ContainerHelpers.ParsePortError parsedErrors = (ContainerHelpers.ParsePortError)errors!;

                if (parsedErrors.HasFlag(ContainerHelpers.ParsePortError.MissingPortNumber))
                {
                    Log.LogErrorWithCodeFromResources(nameof(Strings.MissingPortNumber), port.ItemSpec);
                }
                else
                {
                    if (parsedErrors.HasFlag(ContainerHelpers.ParsePortError.InvalidPortNumber) && parsedErrors.HasFlag(ContainerHelpers.ParsePortError.InvalidPortType))
                    {
                        Log.LogErrorWithCodeFromResources(nameof(Strings.InvalidPort_NumberAndType), portNo, portType);
                    }
                    else if (parsedErrors.HasFlag(ContainerHelpers.ParsePortError.InvalidPortNumber))
                    {
                        Log.LogErrorWithCodeFromResources(nameof(Strings.InvalidPort_Number), portNo);
                    }
                    else if (parsedErrors.HasFlag(ContainerHelpers.ParsePortError.InvalidPortType))
                    {
                        Log.LogErrorWithCodeFromResources(nameof(Strings.InvalidPort_Type), portType);
                    }
                }
            }
        }
    }

    private void SetEnvironmentVariables(ImageBuilder img, ITaskItem[] envVars, ILogger logger)
    {
        foreach (ITaskItem envVar in envVars)
        {
            var value = envVar.GetMetadata("Value");
            img.AddEnvironmentVariable(envVar.ItemSpec, value);
        }
    }

    private void SafeLog(string message, params object[] formatParams) {
        if(BuildEngine != null) Log.LogMessage(MessageImportance.High, message, formatParams);
    }

    public void Dispose()
    {
        _cancellationTokenSource.Dispose();
    }

    internal (string[] entrypoint, string[] cmd) DetermineEntrypointAndCmd(string[]? baseImageEntrypoint)
    {
        string[] entrypoint = Entrypoint.Select(i => i.ItemSpec).ToArray();
        string[] entrypointArgs = EntrypointArgs.Select(i => i.ItemSpec).ToArray();
        string[] cmd = DefaultArgs.Select(i => i.ItemSpec).ToArray();
        string[] appCommand = AppCommand.Select(i => i.ItemSpec).ToArray();
        string[] appCommandArgs = AppCommandArgs.Select(i => i.ItemSpec).ToArray();
        string appCommandInstruction = AppCommandInstruction;

        return ImageBuilder.DetermineEntrypointAndCmd(entrypoint, entrypointArgs, cmd, appCommand, appCommandArgs, appCommandInstruction, baseImageEntrypoint,
            logWarning: s => Log.LogWarningWithCodeFromResources(s),
            logError: (s, a) => { if (a is null) Log.LogErrorWithCodeFromResources(s); else Log.LogErrorWithCodeFromResources(s, a); });
    }
}
