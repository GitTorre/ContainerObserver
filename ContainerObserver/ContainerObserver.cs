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
        public ulong CpuErrorUsageThresholdPct
        {
            get; set;
        }

        public ulong MemErrorUsageThresholdMb
        {
            get; set;
        }

        // Warning thresholds
        public ulong CpuWarningUsageThresholdPct
        {
            get; set;
        }

        public ulong MemWarningUsageThresholdMb
        {
            get; set;
        }

        public ulong MemoryErrorLimitPercent
        {
            get; set;
        }

        public ulong MemoryWarningLimitPercent
        {
            get; set;
        }

        private List<FabricResourceUsageData<ulong>> allCpuData;
        private List<FabricResourceUsageData<ulong>> allMemData;
        private readonly Progress<ContainerStatsResponse> progress;

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

            /*var codepackages = await FabricClientInstance.QueryManager.GetDeployedCodePackageListAsync(
                NodeName,
                new Uri("fabric:/ARRType")).ConfigureAwait(false);

            var codePackages = codepackages.Where(c => c.HostType == HostType.ContainerHost);*/
            
            using DockerClient client = new DockerClientConfiguration().CreateClient();

            // This takes a Filter Dictionary, not sure how to use it...
            // TODO: Figure out the best way to map app service instance to container id (container logs?).
            IList<ContainerListResponse> containers = await client.Containers.ListContainersAsync(
                new ContainersListParameters()
                {
                    Limit = 5,
                });

            if (allCpuData == null)
            {
                allCpuData = new List<FabricResourceUsageData<ulong>>();
            }

            if (allMemData == null)
            {
                allMemData = new List<FabricResourceUsageData<ulong>>();
            }

            foreach (var container in containers)
            {
                token.ThrowIfCancellationRequested();

                Stopwatch monitorTimer = Stopwatch.StartNew();

                var id = container.ID;

                allCpuData.Add(new FabricResourceUsageData<ulong>("CpuUse", $"{id}_cpu"));
                allMemData.Add(new FabricResourceUsageData<ulong>("MemUse", $"{id}_mem"));

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

            await ReportAsync(token).ConfigureAwait(true);

            runTimer.Stop();
            RunDuration = runTimer.Elapsed;
            LastRunDateTime = DateTime.Now;
        }

        public override Task ReportAsync(CancellationToken token)
        {
            var timeToLiveWarning = SetHealthReportTimeToLive();

            foreach (var cpudata in allCpuData)
            {
                ProcessResourceDataReportHealth(
                       cpudata,
                       CpuErrorUsageThresholdPct,
                       CpuWarningUsageThresholdPct,
                       timeToLiveWarning);
            }

            foreach (var memdata in allMemData)
            {
                ProcessResourceDataReportHealth(
                       memdata,
                       MemErrorUsageThresholdMb,
                       MemWarningUsageThresholdMb,
                       timeToLiveWarning);
            }

            return Task.FromResult(1);
        }

        private void Progress_ProgressChanged(object sender, ContainerStatsResponse e)
        {
            // not sure what this value means. assuming percentage of cpu time (it's a ulong, so, not a double... :-).
            // since you are scoped to 1 core, you don't need the per-core list.
            var cpu = e.CPUStats.CPUUsage.TotalUsage;
            
            // if this comes back as bytes, then you need to convert to MB.= or whatever maps to your configuration
            // value for memory in use.
            var mem = e.MemoryStats.PrivateWorkingSet / 1024 / 1024;

            allCpuData.Where(f => f.Id == $"{e.ID}_cpu").FirstOrDefault().Data.Add(cpu);
            allMemData.Where(f => f.Id == $"{e.ID}_mem").FirstOrDefault().Data.Add(mem);
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
                MemErrorUsageThresholdMb = memErrorUsageThresholdMb;
            }

            var errMemPercentUsed = GetSettingParameterValue(
                ConfigurationSectionName,
                ObserverConstants.NodeObserverMemoryUsePercentError);

            if (!string.IsNullOrEmpty(errMemPercentUsed) && ulong.TryParse(errMemPercentUsed, out ulong memoryPercentUsedErrorThreshold))
            {
                MemoryErrorLimitPercent = memoryPercentUsedErrorThreshold;
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
                MemWarningUsageThresholdMb = memWarningUsageThresholdMb;
            }
        }
    }
}
