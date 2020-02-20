// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Agent.Sdk;
using CommandLine;
using Microsoft.VisualStudio.Services.Agent.Util;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Services.Agent.Listener
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            // We can't use the new SocketsHttpHandler for now for both Windows and Linux
            // On linux, Negotiate auth is not working if the TFS url is behind Https
            // On windows, Proxy is not working
            AppContext.SetSwitch("System.Net.Http.UseSocketsHttpHandler", false);
            using (HostContext context = new HostContext("Agent"))
            {
                return MainAsync(context, args).GetAwaiter().GetResult();
            }
        }

        // Return code definition: (this will be used by service host to determine whether it will re-launch agent.listener)
        // 0: Agent exit
        // 1: Terminate failure
        // 2: Retriable failure
        // 3: Exit for self update
        public async static Task<int> MainAsync(IHostContext context, string[] args)
        {
            Tracing trace = context.GetTrace("AgentProcess");
            trace.Info($"Agent package {BuildConstants.AgentPackage.PackageName}.");
            trace.Info($"Running on {PlatformUtil.HostOS} ({PlatformUtil.HostArchitecture}).");
            trace.Info($"RuntimeInformation: {RuntimeInformation.OSDescription}.");
            context.WritePerfCounter("AgentProcessStarted");
            var terminal = context.GetService<ITerminal>();

            // TODO: check that the right supporting tools are available for this platform
            // (replaces the check for build platform vs runtime platform)

            try
            {
                trace.Info($"Version: {BuildConstants.AgentPackage.Version}");
                trace.Info($"Commit: {BuildConstants.Source.CommitHash}");
                trace.Info($"Culture: {CultureInfo.CurrentCulture.Name}");
                trace.Info($"UI Culture: {CultureInfo.CurrentUICulture.Name}");

                // Validate directory permissions.
                string agentDirectory = context.GetDirectory(WellKnownDirectory.Root);
                trace.Info($"Validating directory permissions for: '{agentDirectory}'");
                try
                {
                    IOUtil.ValidateExecutePermission(agentDirectory);
                }
                catch (Exception e)
                {
                    terminal.WriteError(StringUtil.Loc("ErrorOccurred", e.Message));
                    trace.Error(e);
                    return Constants.Agent.ReturnCode.TerminatedError;
                }

                if (PlatformUtil.RunningOnWindows)
                {
                    // Validate PowerShell 3.0 or higher is installed.
                    var powerShellExeUtil = context.GetService<IPowerShellExeUtil>();
                    try
                    {
                        powerShellExeUtil.GetPath();
                    }
                    catch (Exception e)
                    {
                        terminal.WriteError(StringUtil.Loc("ErrorOccurred", e.Message));
                        trace.Error(e);
                        return Constants.Agent.ReturnCode.TerminatedError;
                    }

                    // Validate .NET Framework 4.5 or higher is installed.
                    if (!NetFrameworkUtil.Test(new Version(4, 5), trace))
                    {
                        terminal.WriteError(StringUtil.Loc("MinimumNetFramework"));
                        // warn only, like configurationmanager.cs does. this enables windows edition with just .netcore to work
                    }
                }

                // Add environment variables from .env file
                string envFile = Path.Combine(context.GetDirectory(WellKnownDirectory.Root), ".env");
                if (File.Exists(envFile))
                {
                    var envContents = File.ReadAllLines(envFile);
                    foreach (var env in envContents)
                    {
                        if (!string.IsNullOrEmpty(env) && env.IndexOf('=') > 0)
                        {
                            string envKey = env.Substring(0, env.IndexOf('='));
                            string envValue = env.Substring(env.IndexOf('=') + 1);
                            Environment.SetEnvironmentVariable(envKey, envValue);
                        }
                    }
                }

                Debugger.Launch();
/*
                // Accepted Commands
                Type[] verbTypes = new Type[]
                {
                    typeof(CommandArgs.ConfigureAgent),
                    typeof(CommandArgs.RunAgent),
                    typeof(CommandArgs.UnconfigureAgent),
                    typeof(CommandArgs.WarmUpAgent),
                };

                // We have custom Help / Version functions
                var parser = new Parser(config =>
                    {
                        config.AutoHelp = false;
                        config.AutoVersion = false;

                        // We should consider making this false, but it will break people adding unknown arguments
                        config.IgnoreUnknownArguments = true;
                    }
                );

                // Parse Arugments
                parser
                    .ParseArguments(args, verbTypes)
                    .WithParsed<CommandArgs.ConfigureAgent>(
                        x =>
                        {
                            s_commandArgs = new CommandArgs();
                            s_commandArgs.Configure = x;
                        })
                    .WithParsed<CommandArgs.RunAgent>(
                        x =>
                        {
                            s_commandArgs = new CommandArgs();
                            s_commandArgs.Run = x;
                        })
                    .WithParsed<CommandArgs.UnconfigureAgent>(
                        x =>
                        {
                            s_commandArgs = new CommandArgs();
                            s_commandArgs.Remove = x;
                        })
                    .WithParsed<CommandArgs.WarmUpAgent>(
                        x =>
                        {
                            s_commandArgs = new CommandArgs();
                            s_commandArgs.Warmup = x;
                        })
                    .WithNotParsed(
                        errors =>
                        {
                            terminal.WriteError("Error parsing arguments...");

                            if (errors.Any(error => error is TokenError))
                            {
                                List<string> errorStr = new List<string>();
                                foreach(var error in errors)
                                {
                                    if (error is TokenError tokenError)
                                    {
                                        errorStr.Add(tokenError.Token);
                                    }
                                }

                                terminal.WriteError(
                                    StringUtil.Loc("UnrecognizedCmdArgs", 
                                    string.Join(", ", errorStr)));
                            }
                        });

                // Arguments where not parsed successfully
                if (s_commandArgs == null)
                {
                    return Constants.Agent.ReturnCode.TerminatedError;
                }
                */
                // Defer to the Agent class to execute the command.
                IAgent agent = context.GetService<IAgent>();
                CommandSettings commandSettings = new CommandSettings(context, args, new SystemEnvironment());

                try
                {
                    return await agent.ExecuteCommand(commandSettings);
                }
                catch (OperationCanceledException) when (context.AgentShutdownToken.IsCancellationRequested)
                {
                    trace.Info("Agent execution been cancelled.");
                    return Constants.Agent.ReturnCode.Success;
                }
                catch (NonRetryableException e)
                {
                    terminal.WriteError(StringUtil.Loc("ErrorOccurred", e.Message));
                    trace.Error(e);
                    return Constants.Agent.ReturnCode.TerminatedError;
                }
            }
            catch (Exception e)
            {
                terminal.WriteError(StringUtil.Loc("ErrorOccurred", e.Message));
                trace.Error(e);
                return Constants.Agent.ReturnCode.RetryableError;
            }
        }

        public static CommandArgs s_commandArgs;
    }
}
