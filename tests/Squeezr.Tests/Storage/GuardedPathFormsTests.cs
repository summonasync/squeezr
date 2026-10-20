using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Tests.Indexing;

namespace Squeezr.Tests.Storage;

// A parameter is whatever the caller typed, and the classifier reads the stored shape: an anchored extra answers
// 'not protected' for the repo-qualified and absolute readings of the very same file. The scrub runs those
// readings back through the manifest before it believes the answer, so a name cannot survive the pass that
// just retired the row it belongs to.
public class GuardedPathFormsTests
{
    private const string AnchoredExtra = "infra/VaultKeys.cs";
    private const string Qualified = "demo/infra/VaultKeys.cs";
    private const string FromWorkspace = "repo/infra/VaultKeys.cs";
    private const string PlainExtra = "infra/vaultkeys.cs";

    [Fact]
    public void Names_TheStoredForm_IsProtectedWithoutAnyFolding()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.SetGuardExtra(workspace, AnchoredExtra);
        var forms = Load(workspace);

        forms.NamesAProtectedPath(PlainExtra).ShouldBeTrue();
        // the control this whole class exists for: the same mask, read raw, says nothing is protected
        SecretGuard.IsProtected(Qualified, [AnchoredExtra]).ShouldBeFalse();
    }

    [Fact]
    public void Names_AQualifiedOrWorkspaceRelativeReading_FoldsToTheStoredForm()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.SetGuardExtra(workspace, AnchoredExtra);
        var forms = Load(workspace);

        forms.NamesAProtectedPath(Qualified).ShouldBeTrue();
        forms.NamesAProtectedPath(FromWorkspace).ShouldBeTrue();
        forms.NamesAProtectedPath("demo/src/App.cs").ShouldBeFalse();
    }

    [Fact]
    public void Names_AnAbsoluteReadingUnderEitherRoot_FoldsToTheStoredForm()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.SetGuardExtra(workspace, AnchoredExtra);
        var forms = Load(workspace);

        forms.NamesAProtectedPath(IngestionWorkspaces.Full(workspace, AnchoredExtra)).ShouldBeTrue();
        forms.NamesAProtectedPath(Path.Combine(workspace, AnchoredExtra)).ShouldBeTrue();
        forms.NamesAProtectedPath(IngestionWorkspaces.Full(workspace, "src/App.cs")).ShouldBeFalse();
    }

    [Fact]
    public void Names_APointRelativeReading_FoldsToo()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.SetGuardExtra(workspace, AnchoredExtra);
        var forms = Load(workspace);

        forms.NamesAProtectedPath($"./{AnchoredExtra}").ShouldBeTrue();
        forms.NamesAProtectedPath($"./{Qualified}").ShouldBeTrue();
        SecretGuard.IsProtected($"./{AnchoredExtra}", [AnchoredExtra]).ShouldBeFalse();
    }

    [Fact]
    public void Names_AValueOutsideEveryKnownShape_IsLeftAlone()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.SetGuardExtra(workspace, AnchoredExtra);
        var forms = Load(workspace);

        forms.NamesAProtectedPath("/srv/other-project/infra/VaultKeys.cs").ShouldBeFalse();
        forms.NamesAProtectedPath("how does the loader work").ShouldBeFalse();
        forms.NamesAProtectedPath("infra").ShouldBeFalse();
    }

    [Fact]
    public void Names_ABuiltInStillAnswersInTheQualifiedForm()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        var forms = Load(workspace);

        forms.NamesAProtectedPath("demo/.env").ShouldBeTrue();
        forms.NamesAProtectedPath(IngestionWorkspaces.Full(workspace, "deploy/keys/monkey.pem")).ShouldBeTrue();
        forms.NamesAProtectedPath(SecretGuardFixture.ExamplePath).ShouldBeFalse();
    }

    [Fact]
    public void Load_WithoutAManifest_StillAnswersTheStoredAndAbsoluteForms()
    {
        var workspace = IngestionWorkspaces.NewWorkspaceWithPlainFile();
        File.Delete(SqueezrPaths.ManifestPath(workspace));
        SecretGuardFixture.SetGuardExtra(workspace, AnchoredExtra);
        var forms = Load(workspace);

        forms.NamesAProtectedPath(PlainExtra).ShouldBeTrue();
        forms.NamesAProtectedPath(Path.Combine(workspace, AnchoredExtra)).ShouldBeTrue();
    }

    // The T1 ruling, pinned: a value that names no file is not a shape of some file the guard refused. Folding it
    // through every root would turn one blank parameter into 'deny', and a fold that denies on nothing is an
    // oracle of its own — the shapes list keeps '' and '.' out at load for the same reason.
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("./")]
    [InlineData("..")]
    public void Names_AValueThatNamesNoFile_IsNotSweptIntoDeny(string value)
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.SetGuardExtra(workspace, AnchoredExtra);
        var forms = Load(workspace);

        forms.NamesAProtectedPath(value).ShouldBeFalse();
    }

    // The null posture is the carrier's, not the injected classifier's: the ingestion half denied a null and the
    // reader half threw on one, which is two answers from one predicate. Denied on both planes now, and that is
    // the same deny-wins the fold already practices — an absent value is not a licence to name a file.
    [Fact]
    public void Names_ANullValue_IsDeniedOnTheWritePlane()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.SetGuardExtra(workspace, AnchoredExtra);

        Load(workspace).NamesAProtectedPath(null).ShouldBeTrue();
    }

    [Fact]
    public void Names_ANullValue_IsDeniedOnTheReadPlaneWithoutThrowing()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.SetGuardExtra(workspace, AnchoredExtra);
        var forms = GuardedPathForms.Load(workspace, SecretGuardReader.Load(workspace));

        var answer = false;
        Should.NotThrow(() => answer = forms.NamesAProtectedPath(null));
        answer.ShouldBeTrue();
    }

    // W-4: a manifest this pass cannot read leaves the qualified readings unanswered, and until the reader
    // says so the verdicts simply change. The missing manifest stays silent on purpose — no manifest means
    // no repos, so no shape was lost; a broken one is a degradation and has to be named.
    [Fact]
    public void Load_UnreadableManifest_TheReaderCarriesTheFoldDegradation()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.SetGuardExtra(workspace, AnchoredExtra);
        var guard = SecretGuardReader.Load(workspace);

        File.WriteAllText(SqueezrPaths.ManifestPath(workspace), "null");
        var forms = GuardedPathForms.Load(workspace, guard);

        forms.NamesAProtectedPath(Qualified).ShouldBeFalse();
        guard.Warnings.ShouldNotBeNull().ShouldContain(w =>
            w.StartsWith("could not read workspace manifest (", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_MissingManifest_TheReaderStaysSilent()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.SetGuardExtra(workspace, AnchoredExtra);
        var guard = SecretGuardReader.Load(workspace);

        File.Delete(SqueezrPaths.ManifestPath(workspace));

        GuardedPathForms.Load(workspace, guard);
        guard.Warnings.ShouldBeNull();
    }

    [Fact]
    public void Load_CorruptManifestJson_DegradesInsteadOfThrowingAndSaysSo()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.SetGuardExtra(workspace, AnchoredExtra);
        var guard = SecretGuardReader.Load(workspace);

        File.WriteAllText(SqueezrPaths.ManifestPath(workspace), "{garbage");

        var forms = GuardedPathForms.Load(workspace, guard);
        Should.NotThrow(() => GuardedPathForms.Load(workspace, guard));
        forms.NamesAProtectedPath(Qualified).ShouldBeFalse();
        guard.Warnings.ShouldNotBeNull().ShouldContain(w =>
            w.StartsWith("could not read workspace manifest (", StringComparison.Ordinal));
    }

    private static GuardedPathForms Load(string workspace) =>
        GuardedPathForms.Load(workspace, SecretGuardIngestion.Load(workspace));
}
