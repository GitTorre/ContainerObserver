# ContainerObserver

This is a sample implementation of a [FabricObserver](https://aka.ms/sf/fabricobserver) plugin that monitors dockerd-process-hosted Service Fabric app container instances for CPU and Memory use. It is mostly complete, but requires testing and potential modification for your scenarios. The purpose of this is to show how you could design and implement a FabricObserver plugin to monitor machine resource use for containers that are hosted in a dockerd process in a Windows Service Fabric Cluster. Feel free to modify and submit a pull request if you find bugs or determine something is wrong with the design/impl.  

### FabricObserver Plugin Model  

#### Steps 

- Install [.Net Core 3.1](https://dotnet.microsoft.com/download/dotnet-core/3.1).
- Navigate to top level directory (where FabricObserver.sln lives, for example) then,

For now, you can use the related nugets (choose target OS, framework-dependent or self-contained nupkg file) available [here](https://github.com/microsoft/service-fabric-observer/releases). 

Download the appropriate nupkg to your local machine and update your local nuget.config to include the location of the file on disk.

Create a new .NET Core 3.1 library project, install the nupkg you need for your target OS (Windows in this case.):  

	Framework-dependent = Requires that .NET Core 3.1 is already installed on target machine.

	Self-contained = Includes all the binaries necessary for running .NET Core 3.1 applications on target machine withoout having to install .NET Core 3.1 Runtime.

- Write your observer plugin!

- Build your observer project, drop the output dll into the Data/Plugins folder in FabricObserver/PackageRoot.

- Add a new config section for your observer in FabricObserver/PackageRoot/Config/Settings.xml (see example at bottom of that file)
   Update ApplicationManifest.xml with Parameters if you want to support Application Parameter Updates for your plugin.
   (Look at both FabricObserver/PackageRoot/Config/Settings.xml and FabricObserverApp/ApplicationPackageRoot/ApplicationManifest.xml for several examples of how to do this.)

- Deploy FabricObserver to your cluster. Your new observer will be managed and run just like any other observer.

#### Note: Due to the complexity of unloading plugins at runtime, in order to add or update a plugin, you must redeploy FabricObserver. The problem is easier to solve for new plugins, as this could be done via a Data configuration update, but we have not added support for this yet.


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
            //... Your impl.
        }

        public override async Task ObserveAsync(CancellationToken token)
        {
            //... Your impl.
        }

        public override async Task ReportAsync(CancellationToken token)
        {
            //... Your impl.
        }
    }
 }
```

When you reference the FabricObserver nuget package, you will have access to
all of the public code in FabricObserver. That is, you will have the same capabilities 
that all other observers have. The world is your oyster when it comes to creating your
custom observer to do whatever the underlying platform affords. 

### Note: make sure you know if .NET Core 3.1 is installed on the target server. If it is not, then you must use the SelfContained package. This is very important.

As you can see in this project, there are two key files:

1. ContainerObserver implementation.
2. The IFabricObserverStartup implementation.

For 2., it's designed to be a trivial - and required - implementation:

``` C#
using Microsoft.Extensions.DependencyInjection;
using FabricObserver.Observers;

[assembly: FabricObserver.FabricObserverStartup(typeof(ContainerObserver))]
namespace FabricObserver.Observers
{
    public class ContainerObserverStartup : IFabricObserverStartup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            _ = services.AddScoped(typeof(ObserverBase), typeof(ContainerObserver));
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
$path = "[sourcedir]\ContainerObserver\bin\debug\netcoreapp3.1"
Copy-ServiceFabricApplicationPackage -ApplicationPackagePath $path -CompressPackage -ApplicationPackagePathInImageStore FabricObserverV3 -TimeoutSec 1800
Register-ServiceFabricApplicationType -ApplicationPathInImageStore FabricObserverV3
New-ServiceFabricApplication -ApplicationName fabric:/FabricObserver -ApplicationTypeName FabricObserverType -ApplicationTypeVersion 3.0.5
```
