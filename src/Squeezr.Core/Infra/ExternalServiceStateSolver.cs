namespace Squeezr.Core.Infra;

public static class ExternalServiceStateSolver
{
    // Ownership dominates: if docker attributes the service to our compose project the state is
    // managed regardless of the probe shape - a container mid-start answers nothing yet. A refused
    // connection after the retry budget is the only honest absent; every other probe failure is
    // unknown, and a live endpoint is external with whatever owner docker could name.
    public static ExternalServiceState Solve(ManagedServiceProbe probe, ServiceLiveness liveness, ServiceOwnership ownership)
    {
        if (ownership.Kind == ServiceOwnership.Verdict.Ours)
        {
            return new ExternalServiceState(probe, ServiceProbeState.Managed, ownership.Owner, liveness.Detail);
        }

        return liveness.Kind switch
        {
            ServiceLiveness.Outcome.Answered => new ExternalServiceState(
                probe,
                ServiceProbeState.External,
                ownership.Kind == ServiceOwnership.Verdict.Foreign ? ownership.Owner : null,
                liveness.Detail),
            ServiceLiveness.Outcome.Refused => new ExternalServiceState(probe, ServiceProbeState.Absent, null, null),
            _ => new ExternalServiceState(probe, ServiceProbeState.Unknown, null, liveness.Detail),
        };
    }

    // Probe before attribution (spec §3.3): attribution is consulted only for a live endpoint, so a
    // missing docker daemon cannot mask a natively running service behind a transport error.
    public static async Task<IReadOnlyList<ExternalServiceState>> SolveAsync(
        IReadOnlyList<ManagedServiceProbe> probes,
        ServiceLivenessProbe livenessProbe,
        IServiceOwnershipSource ownershipSource,
        CancellationToken cancellationToken)
    {
        var states = new List<ExternalServiceState>();
        foreach (var probe in probes)
        {
            var liveness = await livenessProbe.ProbeAsync(probe, cancellationToken);
            var ownership = liveness.Kind == ServiceLiveness.Outcome.Answered
                ? await ownershipSource.AttributeAsync(probe, cancellationToken)
                : ServiceOwnership.Unexplained;
            states.Add(Solve(probe, liveness, ownership));
        }

        return states;
    }
}
