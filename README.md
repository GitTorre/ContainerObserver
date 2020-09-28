# ContainerObserver

This is a sample impl of a [FabricObserver plugin](https://github.com/microsoft/service-fabric-observer/tree/master/SampleObserverPlugin) that monitors dockerd-process-hosted Service Fabric app container instances for CPU and Memory use. It is incomplete and requires testing. The purpose of this is to show how you could design and implement a FabricObserver plugin to monitor container resource use for containers that are hosted in a dockerd process in a Windows Service Fabric Cluster. Feel free to modify and submit a pull request if you get this working with changes (and fixes to the design/impl).  

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
    public class MyObserver : ObserverBase
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

1. Your observer implementation.
2. The IFabricObserverStartup implementation.

For 2., it's designed to be a trivial - and required - impl:

``` C#
using FabricObserver.Observers.Interfaces;
using Microsoft.Extensions.DependencyInjection;

[assembly: FabricObserver.FabricObserverStartup(typeof(FabricObserver.Observers.[Name of this class, e.g., MyObserverStartup]))]
namespace FabricObserver.Observers
{
    public class MyObserverStartup : IFabricObserverStartup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            _ = services.AddScoped(typeof(ObserverBase), typeof([Name of the class that holds your observer impl. E.g., MyObserver]));
        }
    }
}
```

When you build your plugin as a .NET Core 3.1 library, copy the dll file into the Data/Plugins folder inside your build output directory. E.g., YourObserverPlugin\bin\Debug\netcoreapp3.1. In fact, this directory will contain what is effectively an sfpkg file and folder structure:  
```
[sourcedir]\SAMPLEOBSERVERPLUGIN\BIN\DEBUG\NETCOREAPP3.1  
│   ApplicationManifest.xml  
|   ContainerObserver.dll  
|   ContainerObserver.pdb  
|   ContainerObserver.deps.json  
│  
└───FabricObserverPkg  
        Code  
        Config  
        Data  
        ServiceManifest.xml        
```
Update Config/Settings.xml with your new observer config settings. This repo already has one for ContainerObserver plugin. Just follow that pattern and add any other configuration parameters for your observer. If you want to configure your observer via an Application Parameter update after it's been deployed, you will need to add the override parameters to FabricObserverApp/ApplicationPackageRoot/ApplicationManifest.xml. You will see several examples of how to do this in that
file. 

You can deploy using the contents of your build out directory - just remove the pdb, json, dll files from the top level directory, so it looks like this:
```
[sourcedir]\ContainerObserver\BIN\DEBUG\NETCOREAPP3.1  
│   ApplicationManifest.xml  
│  
└───FabricObserverPkg  
        Code  
        Config  
        Data  
        ServiceManifest.xml        
```

### Deploy FO from your plugin build folder (assuming you build FO on Windows - the target can be Windows or Linux, of course.): 

* Open an Admin Powershell console.
* Connect to your cluster.
* Set a $path variable to your deployment content
* Copy bits to server
* Register application type
* Create new instance of FO, which contains your observer!
```Powershell
$path = "[sourcedir]\ContainerObserver\bin\debug\netcoreapp3.1"
Copy-ServiceFabricApplicationPackage -ApplicationPackagePath $path -CompressPackage -ApplicationPackagePathInImageStore FabricObserverV3 -TimeoutSec 1800
Register-ServiceFabricApplicationType -ApplicationPathInImageStore FabricObserverV3
New-ServiceFabricApplication -ApplicationName fabric:/FabricObserver -ApplicationTypeName FabricObserverType -ApplicationTypeVersion 3.0.5
```
