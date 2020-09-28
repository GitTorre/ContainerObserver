using System;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Utilities;
using Docker.DotNet;
using Docker.DotNet.Models;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Fabric;

namespace FabricObserver.Observers
{
    public class ContainerObserver : ObserverBase
    {
        // Error thresholds
        public double CpuErrorUsageThresholdPct
        {
            get; set;
        }

        public ulong MemErrorUsageThresholdMB
        {
            get; set;
        }

        // Warning thresholds
        public double CpuWarningUsageThresholdPct
        {
            get; set;
        }

        public ulong MemWarningUsageThresholdMB
        {
            get; set;
        }

        private List<FabricResourceUsageData<double>> allCpuDataPercentage;
        private List<FabricResourceUsageData<ulong>> allMemDataMB;
        private Progress<ContainerStatsResponse> progress;

        public ContainerObserver()
        {
            progress = new Progress<ContainerStatsResponse>();
            progress.ProgressChanged += Progress_ProgressChanged;
        }

        // OsbserverManager passes in a special token to ObserveAsync and ReportAsync that enables it to stop this observer outside of
        // of the SF runtime, but this token will also cancel when the runtime cancels the main token.
        public override async Task ObserveAsync(CancellationToken token)
        {
            // If set, this observer will only run during the supplied interval.
            // See Settings.xml, CertificateObserverConfiguration section, RunInterval parameter for an example.
            if (RunInterval > TimeSpan.MinValue
                && DateTime.Now.Subtract(LastRunDateTime) < RunInterval)
            {
                return;
            }

            Stopwatch runTimer = Stopwatch.StartNew();
            SetThresholdSFromConfiguration();

            // Get deployed network codepackages (deployed code packages in a container network).
            var codepackages = await FabricClientInstance.NetworkManager.GetDeployedNetworkCodePackageListAsync(
                new System.Fabric.Description.DeployedNetworkCodePackageQueryDescription
                {
                    NodeName = NodeName,
                    ApplicationNameFilter = new Uri("fabric:/ARRType"),
                });

            using DockerClient client = new DockerClientConfiguration().CreateClient();

            // This takes a Filter Dictionary, not sure how to use it...
            /*IList<ContainerListResponse> containers = await client.Containers.ListContainersAsync(
                new ContainersListParameters()
                {
                    Limit = 5,
                });*/

            if (allCpuDataPercentage == null)
            {
                allCpuDataPercentage = new List<FabricResourceUsageData<double>>();
            }

            if (allMemDataMB == null)
            {
                allMemDataMB = new List<FabricResourceUsageData<ulong>>();
            }

            foreach (var codepackage in codepackages)
            {
                token.ThrowIfCancellationRequested();

                Stopwatch monitorTimer = Stopwatch.StartNew();

                var id = codepackage.ContainerId;
                var cpuId = $"{id}_cpu";
                var memId = $"{id}_mem";

                // Don't add new fruds if they already exists. These objects live across Observer runs.
                // Note: Their Data members will be cleared by ProcessDataReportHealth.
                if (!allCpuDataPercentage.Any(frud => frud.Id == cpuId))
                {
                    allCpuDataPercentage.Add(new FabricResourceUsageData<double>("CpuUsePercent", cpuId));
                }

                if (!allMemDataMB.Any(frud => frud.Id == memId))
                {
                    allMemDataMB.Add(new FabricResourceUsageData<ulong>("MemUseMB", memId));
                }

                var containerParams = new ContainerStatsParameters
                {
                    Stream = false,
                };

                // This is how long each measurement sequence for each container can last.
                TimeSpan duration = TimeSpan.FromSeconds(15);

                if (ConfigurationSettings.MonitorDuration > TimeSpan.MinValue)
                {
                    duration = ConfigurationSettings.MonitorDuration;
                }

                while (monitorTimer.Elapsed < duration)
                {
                    await client.Containers.GetContainerStatsAsync(id, containerParams, progress, token);
                    await Task.Delay(250);
                }

                monitorTimer.Stop();
                monitorTimer.Reset();
            }

            runTimer.Stop();
            RunDuration = runTimer.Elapsed;
          
            await ReportAsync(token).ConfigureAwait(true);

            LastRunDateTime = DateTime.Now;
        }

        public override Task ReportAsync(CancellationToken token)
        {
            var timeToLiveWarning = SetHealthReportTimeToLive();

            foreach (var cpudata in allCpuDataPercentage)
            {
                ProcessResourceDataReportHealth(
                       cpudata,
                       CpuErrorUsageThresholdPct,
                       CpuWarningUsageThresholdPct,
                       timeToLiveWarning);
            }

            foreach (var memdata in allMemDataMB)
            {
                ProcessResourceDataReportHealth(
                       memdata,
                       MemErrorUsageThresholdMB,
                       MemWarningUsageThresholdMB,
                       timeToLiveWarning);
            }

            return Task.FromResult(1);
        }

        // NOTE: I have not tested this.
        private void Progress_ProgressChanged(object sender, ContainerStatsResponse e)
        {
            // https://github.com/moby/moby/blob/eb131c5383db8cac633919f82abad86c99bffbe5/cli/command/container/stats_helpers.go#L175
            // 1 Tick = 100 nanoseconds.
            uint possIntervals = (uint)e.Read.Subtract(e.PreRead).Ticks / 100; // Start with number of ns intervals.
            possIntervals /= 100; // Convert to number of 100ns intervals.
            possIntervals *= e.NumProcs; // Multiply by the number of processors.

            // Intervals used: pre = *previous* value, which is required to determine the percentage of cpu used by 
            // this container instance at the time when this observation is made.
            ulong intervalsUsed = e.CPUStats.CPUUsage.TotalUsage - e.PreCPUStats.CPUUsage.TotalUsage;
            double cpuPercent = 0.00;

            // Avoid divide-by-zero.
            if (possIntervals > 0)
            {
                cpuPercent = intervalsUsed / possIntervals * 100.0;
            }
  
            // if this comes back as bytes (I *think* it does), then you need to convert to MB.= or whatever maps to your configuration
            // value for memory in use. If it doesn't, then remove converstion from bytes to MB below..
            var mem = e.MemoryStats.PrivateWorkingSet / 1024 / 1024;

            allCpuDataPercentage.Where(f => f.Id == $"{e.ID}_cpu").FirstOrDefault().Data.Add(cpuPercent);
            allMemDataMB.Where(f => f.Id == $"{e.ID}_mem").FirstOrDefault().Data.Add(mem);
        }

        private void SetThresholdSFromConfiguration()
        {
            /* Error thresholds */

            // This is the main SF runtime token, which will be cancelled if, say, this node goes down or
            // this service instance goes down.
            Token.ThrowIfCancellationRequested();

            // This observer uses the same config parameter names as NodeObserver, thus the use of 
            // ObserverConstants.NodeObserverCpuWarningPct, etc..
            var cpuError = GetSettingParameterValue(
                ConfigurationSectionName,
                ObserverConstants.NodeObserverCpuErrorLimitPct);

            if (!string.IsNullOrEmpty(cpuError) && ulong.TryParse(cpuError, out ulong cpuErrorUsageThresholdPct))
            {
                CpuErrorUsageThresholdPct = cpuErrorUsageThresholdPct;
            }

            var memError = GetSettingParameterValue(
                ConfigurationSectionName,
                ObserverConstants.NodeObserverMemoryErrorLimitMb);

            if (!string.IsNullOrEmpty(memError) && ulong.TryParse(memError, out ulong memErrorUsageThresholdMb))
            {
                MemErrorUsageThresholdMB = memErrorUsageThresholdMb;
            }

            /* Warning thresholds */

            Token.ThrowIfCancellationRequested();

            var cpuWarn = GetSettingParameterValue(
                ConfigurationSectionName,
                ObserverConstants.NodeObserverCpuWarningLimitPct);

            if (!string.IsNullOrEmpty(cpuWarn) && ulong.TryParse(cpuWarn, out ulong cpuWarningUsageThresholdPct))
            {
                CpuWarningUsageThresholdPct = cpuWarningUsageThresholdPct;
            }

            var memWarn = GetSettingParameterValue(
                ConfigurationSectionName,
                ObserverConstants.NodeObserverMemoryWarningLimitMb);

            if (!string.IsNullOrEmpty(memWarn) && ulong.TryParse(memWarn, out ulong memWarningUsageThresholdMb))
            {
                MemWarningUsageThresholdMB = memWarningUsageThresholdMb;
            }
        }

        protected override void Dispose(bool disposing)
        {
            progress.ProgressChanged -= Progress_ProgressChanged;
            base.Dispose(disposing);
        }
    }
}
