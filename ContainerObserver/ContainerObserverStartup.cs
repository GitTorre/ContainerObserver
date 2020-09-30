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
