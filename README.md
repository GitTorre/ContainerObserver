# ContainerObserver (For Reference Only)

## NOTE: ContainerObserver now ships with FabricObserver beginning in version 3.1.17. This repo is just for reference on how to build a FabricObserver plugin. 
### If you cloned this repo to build your own ContainerObserver plugin, the current impl is supplied here, so you have the latest. However, if you want to ship your plugin, make sure to disable FO's ContainerObserver and add your plugin to FO as usual. You will need to rename your plugin since ContainerObserver is reserved.  

This is a reference implementation of a [FabricObserver](https://aka.ms/sf/fabricobserver) plugin, built as a .NET Standard 2.0 library, that monitors dockerd-hosted Service Fabric app container instances for CPU and Memory use. It demonstrates how to write FabricObserver plugins that extend FabricObserver's capabilities and then deploy FO to your cluster from the plugin build output directory (which is effectively a decompressed sfpkg). You can learn more about the FabricObserver plugin model [here](https://github.com/microsoft/service-fabric-observer/tree/master/SampleObserverPlugin) and [here](https://github.com/microsoft/service-fabric-observer/blob/master/Documentation/Plugins.md).

### What it does
ContainerObserver monitors and reports on machine resource use - CPU% and Private Working Set MB in this impl (extend based on your needs) - emitting warnings or errors based on configurable threshold values for Service Fabric docker container apps running in Windows and Linux clusters.  

![SFX Warning](/ContainerObserver/SFX.png)  

**Note: ContainerObserver must be hosted in an elevated FabricObserver process on Windows. When you build this project, the related SF configuration files will be set to run FabricObserver as Admin on Windows. See ApplicationManifest_Modified.xml in source folder. 
For Linux, we solved this problem by implementing a Capabilities-laced binary that can only run one command as root, so FabricObserver itself will run as standard user on Linux. Windows does not have anything like Linux's Capabilities model, 
so it seems that running FO as System is the only recourse for Windows deployments.**  

#### Build Steps 

```ContainerObserver requires Microsoft.ServiceFabricApps.FabricObserver nupkg version 3.1.8+**.```

- Install [.Net Core 3.1](https://dotnet.microsoft.com/download/dotnet-core/3.1) if you haven't already.
- Clone the repo.
- Install the FO nupkg into the ContainerObserver project by PackageReference in ContainerObserver.csproj. By Default, it will install the Windows SelfContained package from nuget.org. 
  For Linux, just reference the Linux SelfContained package and change the RuntimeIdentifier in ContainerObserver.csproj to linux-x64. All of the packages are Microsoft-signed. 
  NOTE: Unless you have already installed .NET Core 3.1.x on the target server, you must reference the SelfContained nuget package for the desired OS platform. 
  **If you add new packages to this project to extend capabilities, then, as always, you must place ALL related dependencies into the same folder as ContainerObserver.dll. You can create a new folder under Plugins directory (say, ContainerObserver) and place plugin dll and its dependencies there if you deploy multiple FO plugins, for example.**
- Update the ContainerObserver appTarget and CPU/Mem threshold values in the [containerobserver.config.json](/ContainerObserver/containerobserver.config.json) file. 
- Build.

ContainerObserver only monitors two metrics: container process CPU use (%) and process private memory use (MB). ContainerObserver is designed to work on both Windows and Linux OS platforms. 

If you are going to deploy FabricObserver (with ContainerObserver onboard, of course) to Linux cluster, then you *really* should change the value of the EntryPointType attribute 
of the RunAsPolicy element in ApplicationManifest_Modified.xml to "Setup":  

```XML
    <Policies>
      <RunAsPolicy CodePackageRef="Code" UserRef="SystemUser" EntryPointType="Setup" />
    </Policies>
```
This means that only the setup code can run as root and not the FabricObserver binary. 


#### Configuration  


For configuration, housed in containerobserver.config.json, you can supply 
 
 ```JSON 
 "targetApp":"*",
```  
setting which means all container app targets. You can then filter which apps to target by including a 

```JSON 
 "appIncludeList":"fabric:/someApp, fabric:/someOtherApp", 
 ``` 
 or a  
 
 ```JSON 
"appExcludeList":"fabric:/someApp, fabric:/someOtherApp", 
```

You choose how you want to filter the app target collection. This makes it really easy to specify the same settings for multiple apps. Also, if you supply specific app settings as well that include different values for the same settings supplied in the all-apps config, then the specific ones will override the global ones and the global settings not specified in the specific app target sections will be applied to the specific app targets. 

- Update ContainerObserver basic settings in [ApplicationManifest_Modified.xml](/ContainerObserver/ApplicationManifest_Modified.xml) (this will be renamed to ApplicationManifest.xml and copied to correct location during post-build event step). Also, update ApplicationManifest_Modified.xml's ApplicationTypeVersion and ServiceManifestVersion to match that of the FabricObserver nupkg you're using (these are currently set to 3.1.13, the most recent FO release),
and the parameters for any other observer you care about since you will be deploying FabricObserver with your plugin in place.
**NOTE: For linux deployments, you must modify ContainerObserver.csproj to build linux-x64 (&lt;RuntimeIdentifier&gt;linux-x64&lt;/RuntimeIdentifier&gt;). 

- Build the ContainerObserver project.
- Deploy FabricObserver to your cluster. Your new observer will be managed and run just like any other observer.

The core idea is that writing an observer plugin is an equivalent experience to writing one inside the FabricObserver project itself.

``` C#
using System.Fabric;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;

namespace FabricObserver.Observers
{
    public class ContainerObserver : ObserverBase
    {
        public ContainerObserver(FabricClient fabricClient, StatelessServiceContext context)
            : base(fabricClient, context)
        {
            //... impl.
        }

        public override async Task ObserveAsync(CancellationToken token)
        {
            //... impl.
        }

        public override async Task ReportAsync(CancellationToken token)
        {
            //... impl.
        }
    }
 }
```

When you reference the FabricObserver nuget package, you will have access to all of the public code in FabricObserver. That is, you will have the same capabilities 
that all other observers have. The world is your oyster when it comes to creating your custom observer to do whatever the underlying platform affords. 

As you can see in this project, there are two key files:

1. ContainerObserver.cs.
2. ContainerObserverStartup.cs.

For 2., it's designed to be a trivial - and required - implementation:

``` C#
using Microsoft.Extensions.DependencyInjection;
using FabricObserver.Observers;
using System.Fabric;

[assembly: FabricObserver.FabricObserverStartup(typeof(ContainerObserverStartup))]
namespace FabricObserver.Observers
{
    public class ContainerObserverStartup : IFabricObserverStartup
    {
        public void ConfigureServices(IServiceCollection services, FabricClient fabricClient, StatelessServiceContext context)
        {
            services.AddScoped(typeof(ObserverBase), s => new ContainerObserver(fabricClient, context));
        }
    }
}
```

When you build the ContainerObserver plugin project, all files will be placed into the correct locations in the output directory. E.g., C:\Users\me\source\repos\ContainerObserver\ContainerObserver\bin\Release\netstandard2.0\win-x64. ContainerObserver.dll, Settings.xml, and ApplicationManifest_Modified.xml files will be placed (and renamed in the case of ApplicationManifest) into the correct locations. In fact, this directory will contain what is effectively an sfpkg file and folder structure:  
```
[sourcedir]\ContainerObserver\bin\release\netstandard2.0\win-x64  
│   ApplicationManifest.xml  
│  
└───FabricObserverPkg  
        Code  
        Config  
        Data  
        ServiceManifest.xml        
```

### You are now ready to deploy FabricObserver from your plugin build output folder.

* Open an Admin Powershell console.
* Connect to your cluster.
* Set a $path variable to your deployment content (full path of your build output top level directory).
* Copy bits to server.
* Register application type.
* Create new instance of FabricObserver, which includes your custom observer, which will be run and managed like all other observers.  

Example script: 

```Powershell
$path = "[sourcedir]\ContainerObserver\bin\Debug\netstandard2.0\[win-x64 or linux-x64, depending on your OS target]"
Copy-ServiceFabricApplicationPackage -ApplicationPackagePath $path -CompressPackage -ApplicationPackagePathInImageStore FabricObserverV3117 -TimeoutSec 1800
Register-ServiceFabricApplicationType -ApplicationPathInImageStore FabricObserverV3117
New-ServiceFabricApplication -ApplicationName fabric:/FabricObserver -ApplicationTypeName FabricObserverType -ApplicationTypeVersion 3.1.17
```
