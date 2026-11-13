namespace Squeezr.Core.Storage;

// Why a store file would not open, as data rather than prose. Every value names the observation that
// produced it, and a door that cannot make that observation answers Unattributed rather than guessing:
// review-116-12 W-2 is the rule that a refusal class must be measured, never inferred from the fact that a
// null came back.
//
// The classes here are the ones this box answers with. Measured 2026-11-13 against the LiteDB the product
// ships (5.0.21, ConnectionType.Shared, /var/tmp/t7-probe): a header whose fields do not parse — zeroed or
// garbled behind a standing magic — dies as LiteException with errorCode 103 ('not a valid LiteDB database
// format'), and a file that is merely absent opens lazily rather than refusing, so Missing can only ever
// come from MetaStore's own File.Exists probe. Scrambling bytes anywhere OUTSIDE the header page left the
// database opening and counting its rows, which is why no value here claims more than the header reader
// said. A competing read-write connection opened a second handle in the same process, so this door has no
// measured lock-contention class to name — contention is IndexLock's subject, not the open's.
public enum StoreOpenCause
{
    None,
    Missing,
    Denied,
    Unparseable,
    Unattributed,
}
