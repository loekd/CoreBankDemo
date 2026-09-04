using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace CoreBankDemo.PaymentsAPI;

internal static class PaymentIntakeServiceCollectionExtensions
{
    internal static IServiceCollection AddPaymentIntake(this IServiceCollection services)
    {
        services.AddControllers();
        services.Configure<ApiBehaviorOptions>(options =>
        {
            options.SuppressModelStateInvalidFilter = true;
        });

        return services;
    }

    internal static IEndpointRouteBuilder MapPaymentIntake(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapControllers();
        return endpoints;
    }
}
