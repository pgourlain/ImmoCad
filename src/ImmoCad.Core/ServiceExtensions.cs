using ImmoCad.Core.History;
using Microsoft.Extensions.DependencyInjection;

namespace ImmoCad.Core;

public static class ServiceExtensions
{
    public static IServiceCollection AddImmoCadServices(this IServiceCollection s)
    {
        s.AddSingleton<IBuildingEventsService, BuildingEventsService>();
        return s;
    }
}
