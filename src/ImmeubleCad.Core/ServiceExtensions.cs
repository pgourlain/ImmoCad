using ImmeubleCad.Core.History;
using Microsoft.Extensions.DependencyInjection;

namespace ImmeubleCad.Core;

public static class ServiceExtensions
{
    public static IServiceCollection AddImmeubleCadServices(this IServiceCollection s)
    {
        s.AddSingleton<IBuildingEventsService, BuildingEventsService>();
        return s;
    }
}
