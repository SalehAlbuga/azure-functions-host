﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.AppService.Proxy.Client;
using Microsoft.Azure.WebJobs.Host.Scale;
using Microsoft.Azure.WebJobs.Script.Config;
using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.WebJobs.Script.Scale
{
    /// <summary>
    /// Manages scale monitoring operations.
    /// </summary>
    public class FunctionsScaleManager
    {
        private readonly IScaleMonitorManager _monitorManager;
        private readonly IScaleMetricsRepository _metricsRepository;
        private readonly ITargetScalerManager _targetScalerManager;
        private readonly IConcurrencyStatusRepository _concurrencyStatusRepository;
        private readonly IFunctionsHostingConfiguration _functionsHostingConfiguration;
        private readonly IEnvironment _environment;
        private readonly ILogger _logger;

        // for mock testing only
        public FunctionsScaleManager()
        {
        }

        public FunctionsScaleManager(
            IScaleMonitorManager monitorManager,
            IScaleMetricsRepository metricsRepository,
            ITargetScalerManager targetScalerManager,
            IConcurrencyStatusRepository concurrencyStatusRepository,
            IFunctionsHostingConfiguration functionsHostingConfiguration,
            IEnvironment environment,
            ILoggerFactory loggerFactory)
        {
            _monitorManager = monitorManager;
            _metricsRepository = metricsRepository;
            _targetScalerManager = targetScalerManager;
            _concurrencyStatusRepository = concurrencyStatusRepository;
            _functionsHostingConfiguration = functionsHostingConfiguration;
            _environment = environment;
            _logger = loggerFactory.CreateLogger<FunctionsScaleManager>();
        }

        /// <summary>
        /// Get the current scale status (vote) by querying all active monitors for their
        /// scale status.
        /// </summary>
        /// <param name="context">The context to use for the scale decision.</param>
        /// <returns>The scale vote.</returns>
        public virtual async Task<ScaleStatusResult> GetScaleStatusAsync(ScaleStatusContext context)
        {
            var scaleMonitors = _monitorManager.GetMonitors();
            var targetScalers = _targetScalerManager.GetTargetScalers();

            GetScalersToSample(_environment, _functionsHostingConfiguration, scaleMonitors, targetScalers,
                out List<IScaleMonitor> scaleMonitorsToProcess, out List<ITargetScaler> targetScalersToProcess);

            var scaleMonitorVotes = await GetScaleMonitorsResultAsync(context, scaleMonitorsToProcess);
            var targetScalerVotes = await GetTargetScalersResultAsync(context, targetScalersToProcess);

            return new ScaleStatusResult
            {
                Vote = GetAggregateScaleVote(scaleMonitorVotes.Union(targetScalerVotes.Select(x => x.Vote)), context, _logger),
                TargetWorkerCount = targetScalerVotes.Any() ? targetScalerVotes.Max(x => x.TargetWorkerCount) : null
            };
        }

        private async Task<IEnumerable<ScaleVote>> GetScaleMonitorsResultAsync(ScaleStatusContext context, IEnumerable<IScaleMonitor> scaleMonitorsToProcess)
        {
            List<ScaleVote> votes = new List<ScaleVote>();
            if (scaleMonitorsToProcess.Any())
            {
                // get the collection of current metrics for each monitor
                var monitorMetrics = await _metricsRepository.ReadMetricsAsync(scaleMonitorsToProcess);

                _logger.LogDebug($"Computing scale status (WorkerCount={context.WorkerCount})");
                _logger.LogDebug($"{monitorMetrics.Count} scale monitors to sample");

                // for each monitor, ask it to return its scale status (vote) based on
                // the metrics and context info (e.g. worker count)
                foreach (var pair in monitorMetrics)
                {
                    var monitor = pair.Key;
                    var metrics = pair.Value;

                    try
                    {
                        // create a new context instance to avoid modifying the
                        // incoming context
                        var scaleStatusContext = new ScaleStatusContext
                        {
                            WorkerCount = context.WorkerCount,
                            Metrics = metrics
                        };
                        var result = monitor.GetScaleStatus(scaleStatusContext);

                        _logger.LogDebug($"Monitor '{monitor.Descriptor.Id}' voted '{result.Vote.ToString()}'");
                        votes.Add(result.Vote);
                    }
                    catch (Exception exc) when (!exc.IsFatal())
                    {
                        // if a particular monitor fails, log and continue
                        _logger.LogError(exc, $"Failed to query scale status for monitor '{monitor.Descriptor.Id}'.");
                    }
                }
            }
            else
            {
                // no monitors registered
                // this can happen if the host is offline
            }

            return votes;
        }

        private async Task<IEnumerable<TargetScalerVote>> GetTargetScalersResultAsync(ScaleStatusContext context, IEnumerable<ITargetScaler> targetScalersToProcess)
        {
            List<TargetScalerVote> targetScaleVotes = new List<TargetScalerVote>();

            if (targetScalersToProcess.Any())
            {
                _logger.LogDebug($"{targetScalersToProcess.Count()} target scalers to sample");
                HostConcurrencySnapshot snapshot = null;
                try
                {
                    snapshot = await _concurrencyStatusRepository.ReadAsync(CancellationToken.None);
                }
                catch (Exception exc) when (!exc.IsFatal())
                {
                    _logger.LogError(exc, $"Failed to read concurrency status repository");
                }

                foreach (var targetScaler in targetScalersToProcess)
                {
                    try
                    {
                        TargetScalerContext targetScaleStatusContext = new TargetScalerContext();
                        if (snapshot != null)
                        {
                            if (snapshot.FunctionSnapshots.TryGetValue(targetScaler.TargetScalerDescriptor.FunctionId, out var functionSnapshot))
                            {
                                targetScaleStatusContext.InstanceConcurrency = functionSnapshot.Concurrency;
                                _logger.LogDebug($"Snapshot dynamic concurrency for target scaler '{targetScaler.TargetScalerDescriptor.FunctionId}' is '{functionSnapshot.Concurrency}'");
                            }
                        }

                        TargetScalerResult result = await targetScaler.GetScaleResultAsync(targetScaleStatusContext);
                        _logger.LogDebug($"Target worker count for '{targetScaler.TargetScalerDescriptor.FunctionId}' is '{result.TargetWorkerCount}'");

                        ScaleVote vote = ScaleVote.None;
                        if (context.WorkerCount > result.TargetWorkerCount)
                        {
                            vote = ScaleVote.ScaleIn;
                        }
                        else if (context.WorkerCount < result.TargetWorkerCount)
                        {
                            vote = ScaleVote.ScaleOut;
                        }

                        targetScaleVotes.Add(new TargetScalerVote
                        {
                            TargetWorkerCount = result.TargetWorkerCount,
                            Vote = vote
                        });
                    }
                    catch (Exception exc) when (!exc.IsFatal())
                    {
                        // if a particular target scaler fails, log and continue
                        _logger.LogError(exc, $"Failed to query scale result for target scaler '{targetScaler.TargetScalerDescriptor.FunctionId}'.");
                    }
                }
            }
            return targetScaleVotes;
        }

        internal static ScaleVote GetAggregateScaleVote(IEnumerable<ScaleVote> votes, ScaleStatusContext context, ILogger logger)
        {
            ScaleVote vote = ScaleVote.None;
            if (votes.Any())
            {
                // aggregate all the votes into a single vote
                if (votes.Any(p => p == ScaleVote.ScaleOut))
                {
                    // scale out if at least 1 monitor requires it
                    logger.LogDebug("Scaling out based on votes");
                    vote = ScaleVote.ScaleOut;
                }
                else if (context.WorkerCount > 0 && votes.All(p => p == ScaleVote.ScaleIn))
                {
                    // scale in only if all monitors vote scale in
                    logger.LogDebug("Scaling in based on votes");
                    vote = ScaleVote.ScaleIn;
                }
            }
            else if (context.WorkerCount > 0)
            {
                // if no functions exist or are enabled we'll scale in
                logger.LogDebug("No enabled functions or scale votes so scaling in");
                vote = ScaleVote.ScaleIn;
            }

            return vote;
        }

        /// <summary>
        /// Returns scale monitors and target scalers we want to use based on the configuration.
        /// Scaler monitor will be ignored if a target scaler is defined in the same extensions assembly and TBS is enabled.
        /// </summary>
        /// <param name="environment">Environment variables.</param>
        /// <param name="hostingConfiguration">Hosting configuration.This is used to enable TDS on stamp level for specific triggers.</param>
        /// <param name="scaleMonitors">Registered scale monitors.</param>
        /// <param name="targetScalers">Registered target scalers.</param>
        /// <param name="scaleMonitorsToSample">Scale monitor to process.</param>
        /// <param name="targetScalersToSample">Target scaler to process.</param>
        internal static void GetScalersToSample(
            IEnvironment environment,
            IFunctionsHostingConfiguration hostingConfiguration,
            IEnumerable<IScaleMonitor> scaleMonitors,
            IEnumerable<ITargetScaler> targetScalers,
            out List<IScaleMonitor> scaleMonitorsToSample,
            out List<ITargetScaler> targetScalersToSample)
        {
            scaleMonitorsToSample = new List<IScaleMonitor>();
            targetScalersToSample = new List<ITargetScaler>();

            // Check if TBS enabled on app level
            if (environment.IsTargetBasedScalingEnabled())
            {
                HashSet<string> targetScalerAssemblies = new HashSet<string>();
                foreach (var scaler in targetScalers)
                {
                    string assemblyName = scaler.GetType().Assembly.GetName().Name;
                    string flag = hostingConfiguration.GetValue(assemblyName, null);
                    if (flag == "1")
                    {
                        targetScalersToSample.Add(scaler);
                        targetScalerAssemblies.Add(assemblyName);
                    }
                }

                foreach (var monitor in scaleMonitors)
                {
                    string monitorAssemblyName = monitor.GetType().Assembly.GetName().Name;
                    // Check if there are scale monitor and target scaler defined in the same assembly
                    if (!targetScalerAssemblies.Contains(monitorAssemblyName))
                    {
                        scaleMonitorsToSample.Add(monitor);
                    }
                }
            }
            else
            {
                scaleMonitorsToSample.AddRange(scaleMonitors);
            }
        }
    }
}
