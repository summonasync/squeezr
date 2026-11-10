using System.Text.Json.Serialization;

namespace Squeezr.Core.Storage;

[JsonConverter(typeof(JsonStringEnumConverter<WorkspaceKind>))]
public enum WorkspaceKind
{
    Worktree,
    Nested,
}
