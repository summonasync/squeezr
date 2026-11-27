using Shouldly;
using Squeezr.Cli.Commands;
using Squeezr.Core.Infra;
using Squeezr.Semantic.Infra;

namespace Squeezr.Tests.Cli;

public class ServicesUpDecisionTests
{
    [Fact]
    public void Decide_Managed_SkipsPullAndUp()
    {
        var decision = ServicesUpCommand.Decide(
            State(ManagedServiceProbe.Ollama, ServiceProbeState.Managed), managedOverride: false);

        decision.Action.ShouldBe(ServicesUpCommand.DecisionAction.SkipManaged);
    }

    [Fact]
    public void Decide_ExternalOllama_AdoptsWithOwner()
    {
        var decision = ServicesUpCommand.Decide(
            State(ManagedServiceProbe.Ollama, ServiceProbeState.External, owner: "bobai-ollama-1"),
            managedOverride: false);

        decision.Action.ShouldBe(ServicesUpCommand.DecisionAction.AdoptOllama);
        decision.Owner.ShouldBe("bobai-ollama-1");
    }

    [Fact]
    public void Decide_ExternalQdrant_RefusesWithHint()
    {
        var decision = ServicesUpCommand.Decide(
            State(ManagedServiceProbe.Qdrant, ServiceProbeState.External, owner: "foreign-qdrant"),
            managedOverride: false);

        decision.Action.ShouldBe(ServicesUpCommand.DecisionAction.RefuseExternalQdrant);
    }

    [Fact]
    public void Decide_Unknown_BlocksStart()
    {
        var decision = ServicesUpCommand.Decide(
            State(ManagedServiceProbe.Ollama, ServiceProbeState.Unknown), managedOverride: false);

        decision.Action.ShouldBe(ServicesUpCommand.DecisionAction.BlockUnknown);
    }

    [Fact]
    public void Decide_Absent_Starts()
    {
        var decision = ServicesUpCommand.Decide(
            State(ManagedServiceProbe.Qdrant, ServiceProbeState.Absent), managedOverride: false);

        decision.Action.ShouldBe(ServicesUpCommand.DecisionAction.Start);
    }

    [Fact]
    public void Decide_ManagedOverride_ForcesStartThroughEveryState()
    {
        foreach (var state in new[]
                 {
                     ServiceProbeState.Managed,
                     ServiceProbeState.External,
                     ServiceProbeState.Unknown,
                     ServiceProbeState.Absent
                 })
        {
            ServicesUpCommand.Decide(State(ManagedServiceProbe.Ollama, state), managedOverride: true)
                .Action.ShouldBe(ServicesUpCommand.DecisionAction.Start);
        }
    }

    private static ExternalServiceState State(
        ManagedServiceProbe probe, ServiceProbeState state, string? owner = null) =>
        new(probe, state, owner, null);
}
