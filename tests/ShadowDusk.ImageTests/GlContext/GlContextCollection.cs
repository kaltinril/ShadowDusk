#nullable enable

using Xunit;

namespace ShadowDusk.ImageTests.GlContext;

/// <summary>
/// xUnit collection that every GL-rendering test class joins (via
/// <c>[Collection(GlContextCollection.Name)]</c>) so they all share ONE
/// <see cref="GlContextFixture"/> — a single GLFW init + hidden window for
/// the whole run — and execute sequentially (same collection = no test
/// parallelism).
///
/// <para>
/// <b>Why (found while hardening the Phase 37 soft-skip):</b> the classes
/// previously each used <c>IClassFixture&lt;GlContextFixture&gt;</c>, which
/// creates one fixture PER CLASS, and xUnit runs classes in parallel by
/// default. Concurrent GLFW init/terminate races a Win32 window-class
/// registration ("Failed to register window class: Class already exists"),
/// so on a GPU-equipped Windows host some classes could lose the race, fail
/// context creation, and silently soft-skip-as-pass — the same Finding-B
/// masking, just intermittent and local. One shared collection fixture
/// removes the race entirely (and is required for the
/// <c>SHADOWDUSK_REQUIRE_GL</c> hard gate to be reliable, since the gate
/// turns any lost race into a loud failure).
/// </para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class GlContextCollection : ICollectionFixture<GlContextFixture>
{
    public const string Name = "GlContext";
}
