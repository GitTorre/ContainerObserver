using System;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Utilities;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace FabricObserver.Observers
{
    public class ContainerObserver : ObserverBase
    {
        // Error thresholds
        public double CpuErrorUsageThresholdPct
        {
            get; set;
        }

        public double MemErrorUsageThresholdMB
        {
            get; set;
        }

        // Warning thresholds
        public double CpuWarningUsageThresholdPct
        {
            get; set;
        }

        public double MemWarningUsageThresholdMB
        {
            get; set;
        }

        private List<FabricResourceUsageData<double>> allCpuDataPercentage;
        private List<FabricResourceUsageData<double>> allMemDataMB;

        public ContainerObserver()
        {

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

            var codepackages = await FabricClientInstance.QueryManager.GetDeployedCodePackageListAsync(
                       NodeName,
                       new Uri("fabric:/ContainerFoo")).ConfigureAwait(false);

             /*
                CONTAINER ID        NAME                                                                              CPU %               PRIV WORKING SET    NET I/O             BLOCK I/O
                644e19852fa0        sf-38-e6837395-6951-4559-acbc-98146d9b3480_52adab36-a1c0-4ea6-95b4-67e51498fb4e   0.01%               53.25MiB            1.16MB / 526kB      28.4MB / 25.8MB
             */

            if (allCpuDataPercentage == null)
            {
                allCpuDataPercentage = new List<FabricResourceUsageData<double>>();
            }

            if (allMemDataMB == null)
            {
                allMemDataMB = new List<FabricResourceUsageData<double>>();
            }

            foreach (var codepackage in codepackages.Where(c => c.HostType == System.Fabric.HostType.ContainerHost))
            {
                // This is how long each measurement sequence for each container can last.
                TimeSpan duration = TimeSpan.FromSeconds(10);

                if (ConfigurationSettings.MonitorDuration > TimeSpan.MinValue)
                {
                    duration = ConfigurationSettings.MonitorDuration;
                }

                Stopwatch monitorTimer = Stopwatch.StartNew();

                while (monitorTimer.Elapsed < duration)
                {
                    var ps = new ProcessStartInfo
                    {
                        Arguments = $"/c docker stats --no-stream",
                        FileName = $"{Environment.GetFolderPath(Environment.SpecialFolder.System)}\\cmd.exe",
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardInput = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = false,
                    };

                    using var p = Process.Start(ps);
                    List<string> output = new List<string>();
                    string l;

                    while ((l = p.StandardOutput.ReadLine()) != null)
                    {
                        output.Add(l);
                    }

                    foreach (string line in output)
                    {
                        token.ThrowIfCancellationRequested();

                        if (line.Contains("CONTAINER ID"))
                        {
                            continue;
                        }

                        if (!line.Contains(codepackage.ServicePackageActivationId))
                        {
                            continue;
                        }

                        var id = codepackage.ServicePackageActivationId;
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
                            allMemDataMB.Add(new FabricResourceUsageData<double>("MemUseMB", memId));
                        }

                        var stats = line.Split(" ", StringSplitOptions.RemoveEmptyEntries).ToList();
                        
                        if (stats.Count == 0)
                        {
                            ObserverLogger.LogWarning("docker stats not returning any information.");
                            return;
                        }

                        string containerid = stats[0];

                        foreach (var stat in stats)
                        {
                            token.ThrowIfCancellationRequested();
                   
                            if (stat.Contains("%"))
                            {
                                double cpu_percent = double.Parse(stat.Replace("%", ""));
                                allCpuDataPercentage.Where(f => f.Id == cpuId).FirstOrDefault().Data.Add(cpu_percent);
                                ObserverLogger.LogInfo($"CPU% for container {containerid}: {cpu_percent}");
                            }

                            if (stat.Contains("MiB"))
                            {
                                double mem_working_set_mb = double.Parse(stat.Replace("MiB", ""));
                                allMemDataMB.Where(f => f.Id == memId).FirstOrDefault().Data.Add(mem_working_set_mb);
                                ObserverLogger.LogInfo($"Workingset MB for container {containerid}: {mem_working_set_mb}");
                            }
                        }
                    }

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
            var timeToLive = SetHealthReportTimeToLive();

            foreach (var cpudata in allCpuDataPercentage)
            {
                ProcessResourceDataReportHealth(
                       cpudata,
                       CpuErrorUsageThresholdPct,
                       CpuWarningUsageThresholdPct,
                       timeToLive);
            }

            foreach (var memdata in allMemDataMB)
            {
                ProcessResourceDataReportHealth(
                       memdata,
                       MemErrorUsageThresholdMB,
                       MemWarningUsageThresholdMB,
                       timeToLive);
            }

            return Task.FromResult(1);
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
    }
}
