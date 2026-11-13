using System.Text.Json.Serialization;

namespace Squeezr.Core.Workspace;

// Which end of the family a row sits on. The words are the ones the registry answers with, not git's: a parent is
// the row the family anchor names, a child is any row that names that anchor in its parent column. A checkout
// with no row at all is neither — it is missing, and the listing says so instead of inventing a role for it.
[JsonConverter(typeof(JsonStringEnumConverter<WorktreeFamilyRole>))]
public enum WorktreeFamilyRole
{
    Parent,
    Child,
}
