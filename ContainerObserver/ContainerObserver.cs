using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Fabric.Health;
using System.Fabric.Query;
using System.Fabric;
using System.Runtime.InteropServices;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.MachineInfoModel;

namespace FabricObserver.Observers
{
    public class ContainerObserver : ObserverBase
    {
        private List<FabricResourceUsageData<double>> allCpuDataPercentage;
        private List<FabricResourceUsageData<double>> allMemDataMB;

        // userTargetList is the list of ApplicationInfo objects representing apps supplied in configuration.
        private List<ApplicationInfo> userTargetList;

        // deployedTargetList is the list of ApplicationInfo objects representing currently deployed applications in the user-supplied list.
        private List<ApplicationInfo> deployedTargetList;
        private List<ReplicaOrInstanceMonitoringInfo> replicaOrInstanceList;
        private string ConfigurationFilePath = string.Empty;

        public string ConfigPackagePath
        {
            get; set;
        }

        public ContainerObserver(FabricClient fabricClient, StatelessServiceContext context)
            : base(fabricClient, context)
        {
            var configSettings = new MachineInfoModel.ConfigSettings(context);
            ConfigPackagePath = configSettings.ConfigPackagePath;
            userTargetList = new List<ApplicationInfo>();
            deployedTargetList = new List<ApplicationInfo>();
            replicaOrInstanceList = new List<ReplicaOrInstanceMonitoringInfo>(); 
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

            if (!await InitializeAsync(token).ConfigureAwait(false))
            {
                return;
            }

             /*
                CONTAINER ID        NAME                                                                              CPU %               PRIV WORKING SET    NET I/O             BLOCK I/O
                644e19852fa0        sf-38-e6837395-6951-4559-acbc-98146d9b3480_52adab36-a1c0-4ea6-95b4-67e51498fb4e   0.01%               53.25MiB            1.16MB / 526kB      28.4MB / 25.8MB
             */

            foreach (var repOrInst in replicaOrInstanceList)
            {
                // This is how long each measurement sequence for each container can last.
                TimeSpan duration = TimeSpan.FromSeconds(10);
                string serviceName = repOrInst.ServiceName.OriginalString.Replace(repOrInst.ApplicationName.OriginalString, "").Replace("/", "");
                string cpuId = $"{serviceName}_cpu";
                string memId = $"{serviceName}_mem";
                string containerId = string.Empty;

                if (!allCpuDataPercentage.Any(frud => frud.Id == cpuId))
                {
                    allCpuDataPercentage.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.TotalCpuTime, cpuId));
                }

                if (!allMemDataMB.Any(frud => frud.Id == memId))
                {
                    allMemDataMB.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.TotalMemoryConsumptionMb, memId));
                }

                if (ConfigurationSettings.MonitorDuration > TimeSpan.MinValue)
                {
                    duration = ConfigurationSettings.MonitorDuration;
                }

                Stopwatch monitorTimer = Stopwatch.StartNew();
                string args = "/c docker stats --no-stream";
                string filename = $"{Environment.GetFolderPath(Environment.SpecialFolder.System)}\\cmd.exe";
               
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    args = string.Empty;
                    
                    // We need the full path to the currently deployed FO CodePackage, which is where our 
                    // linux capabilities-laced proxy binary lives, which is used for elevated_docker_stats call.
                    string path = FabricServiceContext.CodePackageActivationContext.GetCodePackageObject("Code").Path;
                    filename = $"{path}/elevated_docker_stats";
                }

                while (monitorTimer.Elapsed < duration)
                {
                    ProcessStartInfo ps = new ProcessStartInfo
                    {
                        Arguments = args,
                        FileName = filename,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardInput = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = false,
                    };

                    using (Process p = Process.Start(ps))
                    {
                        List<string> output = new List<string>();
                        string l;

                        while ((l = p.StandardOutput.ReadLine()) != null)
                        {
                            output.Add(l);
                        }

                        foreach (string line in output)
                        {
                            token.ThrowIfCancellationRequested();

                            if (!line.Contains(repOrInst.ServicePackageActivationId))
                            {
                                continue;
                            }

                            List<string> stats = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();

                            if (stats.Count == 0)
                            {
                                ObserverLogger.LogWarning("docker stats not returning any information.");
                                return;
                            }

                            containerId = stats[0];
                            repOrInst.ContainerId = containerId;

                            foreach (string stat in stats)
                            {
                                token.ThrowIfCancellationRequested();

                                if (stat.Contains("%"))
                                {
                                    double cpu_percent = double.Parse(stat.Replace("%", ""));
                                    allCpuDataPercentage.Where(f => f.Id == cpuId).FirstOrDefault().Data.Add(cpu_percent);
                                    ObserverLogger.LogInfo($"CPU% for container {containerId}: {cpu_percent}");
                                }

                                if (stat.Contains("MiB"))
                                {
                                    double mem_working_set_mb = double.Parse(stat.Replace("MiB", ""));
                                    allMemDataMB.Where(f => f.Id == memId).FirstOrDefault().Data.Add(mem_working_set_mb);
                                    ObserverLogger.LogInfo($"Workingset MB for container {containerId}: {mem_working_set_mb}");
                                }
                            }

                            await Task.Delay(250);
                        }
                    }
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
            TimeSpan timeToLive = GetHealthReportTimeToLive();

            foreach (var app in deployedTargetList)
            {
                foreach (var repOrInst in replicaOrInstanceList.Where(rep => rep.ApplicationName.OriginalString == app.TargetApp))
                {
                    string serviceName = repOrInst.ServiceName.OriginalString.Replace(app.TargetApp, "").Replace("/", "");
                    string cpuId = $"{serviceName}_cpu";
                    string memId = $"{serviceName}_mem";
                    string healthReportPropCpu = $"{cpuId}_{NodeName}";
                    string healthReportPropMem = $"{memId}_{NodeName}";
                    var cpuFrudInst = allCpuDataPercentage.Find(cpu => cpu.Id == cpuId);
                    var memFrudInst = allMemDataMB.Find(mem => mem.Id == memId);

                    ProcessResourceDataReportHealth(
                                cpuFrudInst,
                                app.CpuErrorLimitPercent,
                                app.CpuWarningLimitPercent,
                                timeToLive,
                                HealthReportType.Application,
                                repOrInst);

                    ProcessResourceDataReportHealth(
                                memFrudInst,
                                app.MemoryErrorLimitMb,
                                app.MemoryWarningLimitMb,
                                timeToLive,
                                HealthReportType.Application,
                                repOrInst); 
                }
            }

            return Task.CompletedTask;
        }

        // Runs each time ObserveAsync is run to ensure that any new app targets and config changes will
        // be up to date across observer loop iterations.
        private async Task<bool> InitializeAsync(CancellationToken token)
        {
            SetConfigurationFilePath();

            if (!File.Exists(ConfigurationFilePath))
            {
                WriteToLogWithLevel(
                    ObserverName,
                    $"Will not observe resource consumption as no configuration parameters have been supplied. | {NodeName}",
                    LogLevel.Warning);

                return false;
            }

            // This code runs each time ObserveAsync is called,
            // so clear app list and deployed replica/instance list in case a new app has been added to watch list.
            if (userTargetList.Count > 0)
            {
                userTargetList.Clear();
            }

            if (deployedTargetList.Count > 0)
            {
                deployedTargetList.Clear();
            }

            if (replicaOrInstanceList.Count > 0)
            {
                replicaOrInstanceList.Clear();
            }

            if (allCpuDataPercentage == null)
            {
                allCpuDataPercentage = new List<FabricResourceUsageData<double>>();
            }

            if (allMemDataMB == null)
            {
                allMemDataMB = new List<FabricResourceUsageData<double>>();
            }

            using (Stream stream = new FileStream(ConfigurationFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > 0
                    && JsonHelper.IsJson<List<ApplicationInfo>>(File.ReadAllText(ConfigurationFilePath)))
                {
                    userTargetList.AddRange(JsonHelper.ReadFromJsonStream<ApplicationInfo[]>(stream));
                }
            }

            // Are any of the config-supplied apps deployed?.
            if (userTargetList.Count == 0)
            {
                WriteToLogWithLevel(
                    ObserverName,
                    $"Will not observe resource consumption as no configuration parameters have been supplied. | {NodeName}",
                    LogLevel.Warning);

                return false;
            }

            int settingSFail = 0;

            foreach (var application in userTargetList)
            {
                token.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(application.TargetApp)
                    && string.IsNullOrWhiteSpace(application.TargetAppType))
                {
                    HealthReporter.ReportFabricObserverServiceHealth(
                        FabricServiceContext.ServiceName.ToString(),
                        ObserverName,
                        HealthState.Warning,
                        $"InitializeAsync | {application.TargetApp}: Required setting, target, is not set.");

                    settingSFail++;

                    continue;
                }

                // No required settings supplied for deployed application(s).
                if (settingSFail == userTargetList.Count)
                {
                    return false;
                }

                try
                {
                    var codepackages = await FabricClientInstance.QueryManager.GetDeployedCodePackageListAsync(
                            NodeName,
                            new Uri(application.TargetApp),
                            null,
                            null,
                            TimeSpan.FromSeconds(30),
                            token).ConfigureAwait(false);

                    if (codepackages.Count == 0)
                    {
                        continue;
                    }

                    var containerhostedPkgs = codepackages.Where(c => c.HostType == HostType.ContainerHost);

                    if (containerhostedPkgs.Count() == 0)
                    {
                        continue;
                    }

                    deployedTargetList.Add(application);

                    await SetInstanceOrReplicaMonitoringList(
                        new Uri(application.TargetApp),
                        null,
                        ServiceFilterType.None,
                        null).ConfigureAwait(false);
                }
                catch (Exception e) when (e is FabricException|| e is TimeoutException || e is OperationCanceledException)
                {
                }
            }

            foreach (var app in deployedTargetList)
            {
                token.ThrowIfCancellationRequested();

                ObserverLogger.LogInfo(
                    $"Will observe container instance resource consumption by {app.TargetApp} " +
                    $"on Node {NodeName}.");
            }

            return true;
        }

        private void SetConfigurationFilePath()
        {
            string configDataFilename = GetSettingParameterValue(
                ConfigurationSectionName,
                "ConfigFileName");

            if (!string.IsNullOrEmpty(configDataFilename) 
                && !ConfigurationFilePath.Contains(configDataFilename))
            {
                ConfigurationFilePath = Path.Combine(ConfigPackagePath, configDataFilename);
            }
        }

        private async Task SetInstanceOrReplicaMonitoringList(
            Uri appName,
            List<string> serviceFilterList,
            ServiceFilterType filterType,
            string appTypeName)
        {
            var deployedReplicaList = await FabricClientInstance.QueryManager.GetDeployedReplicaListAsync(NodeName, appName).ConfigureAwait(true);

            foreach (var deployedReplica in deployedReplicaList)
            {
                ReplicaOrInstanceMonitoringInfo replicaInfo = null;

                if (deployedReplica is DeployedStatefulServiceReplica statefulReplica
                    && statefulReplica.ReplicaRole == ReplicaRole.Primary)
                {
                    replicaInfo = new ReplicaOrInstanceMonitoringInfo()
                    {
                        ApplicationName = appName,
                        ApplicationTypeName = appTypeName,
                        HostProcessId = statefulReplica.HostProcessId,
                        ReplicaOrInstanceId = statefulReplica.ReplicaId,
                        PartitionId = statefulReplica.Partitionid,
                        ServiceName = statefulReplica.ServiceName,
                        ServicePackageActivationId = statefulReplica.ServicePackageActivationId,
                    };

                    if (serviceFilterList != null
                        && filterType != ServiceFilterType.None)
                    {
                        bool isInFilterList = serviceFilterList.Any(s => statefulReplica.ServiceName.OriginalString.ToLower().Contains(s.ToLower()));

                        switch (filterType)
                        {
                            case ServiceFilterType.Include when !isInFilterList:
                            case ServiceFilterType.Exclude when isInFilterList:
                                continue;
                        }
                    }
                }
                else if (deployedReplica is DeployedStatelessServiceInstance statelessInstance)
                {
                    replicaInfo = new ReplicaOrInstanceMonitoringInfo()
                    {
                        ApplicationName = appName,
                        ApplicationTypeName = appTypeName,
                        HostProcessId = statelessInstance.HostProcessId,
                        ReplicaOrInstanceId = statelessInstance.InstanceId,
                        PartitionId = statelessInstance.Partitionid,
                        ServiceName = statelessInstance.ServiceName,
                        ServicePackageActivationId = statelessInstance.ServicePackageActivationId,
                    };

                    if (serviceFilterList != null
                        && filterType != ServiceFilterType.None)
                    {
                        bool isInFilterList = serviceFilterList.Any(s => statelessInstance.ServiceName.OriginalString.ToLower().Contains(s.ToLower()));

                        switch (filterType)
                        {
                            case ServiceFilterType.Include when !isInFilterList:
                            case ServiceFilterType.Exclude when isInFilterList:
                                continue;
                        }
                    }
                }
                
                replicaOrInstanceList.Add(replicaInfo);
            }
        }
    }
}
