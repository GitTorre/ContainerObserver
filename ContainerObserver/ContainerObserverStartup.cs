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
