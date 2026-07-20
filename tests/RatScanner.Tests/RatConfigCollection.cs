using Xunit;

namespace RatScanner.Tests;

/// <summary>
/// Serializes every test class that mutates <see cref="RatConfig"/> static state
/// (e.g. <c>RatConfig.Tracking.TarkovTracker</c> tokens, sources, flags) so xUnit
/// cannot run them in parallel and race on the shared singletons.
/// </summary>
[CollectionDefinition(Name)]
public sealed class RatConfigCollection
{
    public const string Name = "RatConfig static state";
}
