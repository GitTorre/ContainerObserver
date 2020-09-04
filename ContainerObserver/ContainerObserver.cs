using System;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Utilities;
using Docker.DotNet;
using Docker.DotNet.Models;
using System.Collections.Generic;
using System.Diagnostics;

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

        private FabricResourceUsageData<ulong> cpuData;
        private FabricResourceUsageData<ulong> memData;
        private readonly Progress<ContainerStatsResponse> progress;

        public ContainerObserver()
        {
            progress = new Progress<ContainerStatsResponse>();
            progress.ProgressChanged += Progress_ProgressChanged;
        }

        public override async Task ObserveAsync(CancellationToken token)
        {
            // If set, this observer will only run during the supplied interval.
            // See Settings.xml, CertificateObserverConfiguration section, RunInterval parameter for an example.
            if (this.RunInterval > TimeSpan.MinValue
                && DateTime.Now.Subtract(this.LastRunDateTime) < this.RunInterval)
            {
                return;
            }

            SetThresholdSFromConfiguration();

            DockerClient client = new DockerClientConfiguration().CreateClient();
            IList<ContainerListResponse> containers = await client.Containers.ListContainersAsync(
                new ContainersListParameters()
                {
                    Limit = 1,
                });

            if (cpuData == null)
            {
                cpuData = new FabricResourceUsageData<ulong>("CpuUse", $"{containers[0].ID}_cpu", DataCapacity, UseCircularBuffer);
            }

            if (memData == null)
            {
                memData = new FabricResourceUsageData<ulong>("MemUse", $"{containers[0].ID}_mem", DataCapacity, UseCircularBuffer);
            }

            var containerParams = new ContainerStatsParameters
            {
                Stream = false,
            };

            Stopwatch stopwatch = Stopwatch.StartNew();
            TimeSpan duration = TimeSpan.FromSeconds(30);

            if (ConfigurationSettings.MonitorDuration > TimeSpan.MinValue)
            {
                duration = ConfigurationSettings.MonitorDuration;
            }

            while (stopwatch.Elapsed < duration)
            {
                await client.Containers.GetContainerStatsAsync(containers[0].ID, containerParams, progress, token);
                await Task.Delay(250);
            }

            stopwatch.Stop();
            RunDuration = stopwatch.Elapsed;
            stopwatch.Reset();

            await ReportAsync(token).ConfigureAwait(true);
            LastRunDateTime = DateTime.Now;
        }

        public override async Task ReportAsync(CancellationToken token)
        {
            var timeToLiveWarning = this.SetHealthReportTimeToLive();

            this.ProcessResourceDataReportHealth(
                   cpuData,
                   this.CpuErrorUsageThresholdPct,
                   this.CpuWarningUsageThresholdPct,
                   timeToLiveWarning);

            this.ProcessResourceDataReportHealth(
                   memData,
                   this.MemErrorUsageThresholdMb,
                   this.MemWarningUsageThresholdMb,
                   timeToLiveWarning);

            await Task.Delay(42);
        }

        private void Progress_ProgressChanged(object sender, ContainerStatsResponse e)
        {
            // not sure what this value means. assuming percentage of cpu time (it's a ulong, so, not a double... :-).
            // since you are scoped to 1 core, you don't need the per-core list.
            var cpu = e.CPUStats.CPUUsage.TotalUsage;
            // if this comes back as bytes, then you need to convert to MB.
            var mem = e.MemoryStats.PrivateWorkingSet / 1024 / 1024;
            cpuData?.Data.Add(cpu);
            memData?.Data.Add(mem);
        }

        private void SetThresholdSFromConfiguration()
        {
            /* Error thresholds */

            this.Token.ThrowIfCancellationRequested();

            var cpuError = this.GetSettingParameterValue(
                this.ConfigurationSectionName,
                ObserverConstants.NodeObserverCpuErrorLimitPct);

            if (!string.IsNullOrEmpty(cpuError) && ulong.TryParse(cpuError, out ulong cpuErrorUsageThresholdPct))
            {
                this.CpuErrorUsageThresholdPct = cpuErrorUsageThresholdPct;
            }

            var memError = this.GetSettingParameterValue(
                this.ConfigurationSectionName,
                ObserverConstants.NodeObserverMemoryErrorLimitMb);

            if (!string.IsNullOrEmpty(memError) && ulong.TryParse(memError, out ulong memErrorUsageThresholdMb))
            {
                this.MemErrorUsageThresholdMb = memErrorUsageThresholdMb;
            }

            var errMemPercentUsed = this.GetSettingParameterValue(
                this.ConfigurationSectionName,
                ObserverConstants.NodeObserverMemoryUsePercentError);

            if (!string.IsNullOrEmpty(errMemPercentUsed) && ulong.TryParse(errMemPercentUsed, out ulong memoryPercentUsedErrorThreshold))
            {
                this.MemoryErrorLimitPercent = memoryPercentUsedErrorThreshold;
            }

            /* Warning thresholds */

            this.Token.ThrowIfCancellationRequested();

            var cpuWarn = this.GetSettingParameterValue(
                this.ConfigurationSectionName,
                ObserverConstants.NodeObserverCpuWarningLimitPct);

            if (!string.IsNullOrEmpty(cpuWarn) && ulong.TryParse(cpuWarn, out ulong cpuWarningUsageThresholdPct))
            {
                this.CpuWarningUsageThresholdPct = cpuWarningUsageThresholdPct;
            }

            var memWarn = this.GetSettingParameterValue(
                this.ConfigurationSectionName,
                ObserverConstants.NodeObserverMemoryWarningLimitMb);

            if (!string.IsNullOrEmpty(memWarn) && ulong.TryParse(memWarn, out ulong memWarningUsageThresholdMb))
            {
                this.MemWarningUsageThresholdMb = memWarningUsageThresholdMb;
            }
        }
    }
}
