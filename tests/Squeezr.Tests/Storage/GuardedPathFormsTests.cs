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

    private static GuardedPathForms Load(string workspace) =>
        GuardedPathForms.Load(workspace, SecretGuardIngestion.Load(workspace));
}
