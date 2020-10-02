# ContainerObserver

This is an implementation of a [FabricObserver](https://aka.ms/sf/fabricobserver) plugin that monitors dockerd-process-hosted Service Fabric app container instances for CPU and Memory use. It demonstrates how to write FabricObserver plugins that extend FabricObserver's capabilities and then deploy FO to your cluster from the plugin build output directory (which is effectively a decompressed sfpkg).

### What it does
ContainerObserver monitors and reports on machine resource use - CPU% and Private Working Set MB in this impl (extend based on your needs) - emitting warnings or errors based on configurable threshold values for dockerd container apps running in Windows and Linux Service Fabric Clusters.

### FabricObserver Plugin Model  

#### Steps 
- Clone repo.
- Install [.Net Core 3.1](https://dotnet.microsoft.com/download/dotnet-core/3.1).
- Download and install the latest Windows or Linux SelfContained nupkg file from the [FabricObserver repo's Releases section](https://github.com/microsoft/service-fabric-observer/releases).  
- Update the ContainerObserver CPU/Mem threshold values in ApplicationManifest_Modified.xml file (this will be renamed to ApplicationManifest.xml and copied to correct location during post-build event step). Also, update parameters for any other observer you care about since you will be deploying FabricObserver with your plugin in place.  

NOTE: For linux deployments, you must modify ContainerObserver.csproj to build linux-x64 (&lt;RuntimeIdentifier&gt;linux-x64&lt;/RuntimeIdentifier&gt;) also add the following to ApplicationManifest_Modified.xml: 
```xml
    <Policies>
      <RunAsPolicy CodePackageRef="Code" UserRef="SystemUser" EntryPointType="Setup" />
    </Policies>
```

- Build the ContainerObserver project.
- Deploy FabricObserver to your cluster. Your new observer will be managed and run just like any other observer.

The core idea is that writing an observer plugin is an equivalent experience to writing one inside the FabricObserver project itself.

``` C#

using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;

namespace FabricObserver.Observers
{
    public class ContainerObserver : ObserverBase
    {
        public SampleNewObserver()
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

### Note: make sure you know if .NET Core 3.1 is installed on the target server. If it is not, then you must use the SelfContained package. This is very important.

As you can see in this project, there are two key files:

1. ContainerObserver.cs.
2. ContainerObserverStartup.cs.

For 2., it's designed to be a trivial - and required - implementation:

``` C#
using Microsoft.Extensions.DependencyInjection;
using FabricObserver.Observers;

[assembly: FabricObserver.FabricObserverStartup(typeof(ContainerObserverStartup))]
namespace FabricObserver.Observers
{
    public class ContainerObserverStartup : IFabricObserverStartup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddScoped(typeof(ObserverBase), typeof(ContainerObserver));
        }
    }
}
```

When you build the ContainerObserver plugin project, all files will be placed into the correct locations in the output directory. E.g., C:\Users\me\source\repos\ContainerObserver\ContainerObserver\bin\Release\netcoreapp3.1. ContainerObserver.dll, Settings.xml, and ApplicationManifest_Modified.xml files will be placed (and renamed in the case of ApplicationManifest) into the correct locations. In fact, this directory will contain what is effectively an sfpkg file and folder structure:  
```
[sourcedir]\ContainerObserver\bin\release\netcoreapp3.1  
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
$path = "[sourcedir]\ContainerObserver\bin\Debug\netcoreapp3.1\[win-x64 or linux-x64, depending on your build target...]"
Copy-ServiceFabricApplicationPackage -ApplicationPackagePath $path -CompressPackage -ApplicationPackagePathInImageStore FabricObserverV3 -TimeoutSec 1800
Register-ServiceFabricApplicationType -ApplicationPathInImageStore FabricObserverV3
New-ServiceFabricApplication -ApplicationName fabric:/FabricObserver -ApplicationTypeName FabricObserverType -ApplicationTypeVersion 3.0.6
```
