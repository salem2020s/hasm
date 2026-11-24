using Hasm.Api.Company;
using Hasm.Infrastructure.Commercial;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Hasm.Api.Tests;

/// <summary>
/// The activation gate of the short close (Phase 3.13, D-201/D-254; team decision D-267 and the owner item SHORT-CLOSE-READINGS of <c>docs/INTEGRATION-STATUS.md</c>): the PRODUCTION composition registers no
/// document type that may be closed short, so the action is inert in production (the service answers <c>short_close_not_allowed_for_type</c> for every document) until the owner has answered the open readings
/// and a concrete order type is registered as closable. A change that registers a closable type in production fails here and must come with the owner's recorded answers.
/// </summary>
public class ShortCloseProductionGateTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public void The_production_composition_registers_no_document_type_that_may_be_closed_short()
    {
        // the real host (Program.cs), with nothing replaced: every registered registry, and the one the services receive (the last registration wins)
        var registries = factory.Services.GetServices<ICommercialDocumentTypeRegistry>().ToList();
        Assert.NotEmpty(registries);
        Assert.All(registries, r => Assert.Empty(r.ShortCloseTypes));
        var effective = factory.Services.GetRequiredService<ICommercialDocumentTypeRegistry>();
        Assert.Empty(effective.ShortCloseTypes);
        Assert.All(effective.Types, t => Assert.False(effective.IsShortCloseAllowed(t)));       // whatever types production registers one day, none is closable
        Assert.False(effective.IsShortCloseAllowed("commercial_test"));                         // the test-composition type does not exist in production

        // the registration method itself (what Program.cs calls)
        var services = new ServiceCollection().AddHasmCommercial();
        var own = services.Where(d => d.ServiceType == typeof(ICommercialDocumentTypeRegistry)).ToList();
        var registry = Assert.IsAssignableFrom<ICommercialDocumentTypeRegistry>(Assert.Single(own).ImplementationInstance);
        Assert.Empty(registry.ShortCloseTypes);
    }
}
