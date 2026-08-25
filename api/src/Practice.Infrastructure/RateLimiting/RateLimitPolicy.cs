namespace Practice.Infrastructure.RateLimiting;

/// <summary>
/// How many requests a partition may make in a window, and whether the refusal says when
/// the window ends.
/// </summary>
/// <param name="Name">
/// Appears in the partition key and in the audit row's metadata, so it is a stable
/// vocabulary rather than a display string: renaming one starts every partition's count
/// again from zero and orphans the rows already on file.
/// </param>
/// <param name="AnnouncesRetryAfter">
/// WHETHER THE 429 CARRIES A <c>Retry-After</c> HEADER, WHICH IS A DECISION PER POLICY AND
/// NOT A STYLE CHOICE (D098).
///
/// The header helps a legitimate client — a resumable uploader that knows when to come back
/// stops hammering — and it helps an attacker pace exactly at the limit with no wasted
/// requests. Which of those dominates depends on who the caller is:
///
///   login             UNAUTHENTICATED, and the caller is whoever reached the form. There
///                     is no legitimate client that needs a number: <c>web</c> renders one
///                     fixed sentence, and Michelle retries when she retries. Meanwhile the
///                     header is one more field that would have to be proved identical for
///                     an address that has an account and one that does not, on a path
///                     whose whole difficulty is that it must not become an enumeration
///                     oracle (1.18 F1 measured that class in three dimensions at once). So
///                     the refusal carries no header at all, and the dimension cannot leak
///                     because it does not exist.
///
///   dictation-upload  AUTHENTICATED, and the caller is a chunked resumable uploader with a
///                     take it must eventually land. There is no "does this account exist"
///                     question left to answer — the session is already proved — so the
///                     header leaks nothing, and withholding it turns a client that would
///                     have waited into one that retries blind against a container that
///                     scales from zero.
/// </param>
public sealed record RateLimitPolicy(
    string Name, int Limit, TimeSpan Window, bool AnnouncesRetryAfter);

/// <summary>
/// The policies this application enforces, and the numbers behind them.
///
/// A CLASS RATHER THAN LITERALS AT THE CALL SITES, for the reason
/// <see cref="Practice.Infrastructure.Persistence.DatabaseTimeouts"/> exists: a limit is a
/// decision about how much of somebody else's money an attacker is allowed to spend, and a
/// decision written inline in a route registration is not reviewable next to the others.
///
/// REGISTERED AS A SINGLETON SO A TEST CAN REPLACE IT. Two tests in
/// <c>AuthenticationTests</c> drive more attempts than a production limit permits, on
/// purpose — they measure the LOCKOUT under concurrency, which is a different control — and
/// a test that could not raise the limiter out of its own way would end up measuring the
/// limiter instead and reporting the lockout as fixed.
/// </summary>
public sealed class RateLimitPolicies
{
    /// <summary>
    /// Every credential-checking request from one source: 20 in 5 minutes.
    ///
    /// Michelle's own worst day is three — a mistyped password, a corrected one, and a TOTP
    /// code — so twenty leaves room for a fumbled phone and is nowhere near a number she
    /// can reach. From the other side it is 5,760 guesses a day from one address, which is
    /// not a bound worth having on its own and is not sold as one: what it does is make a
    /// single-source flood cost the attacker distribution, and make
    /// <see cref="LoginPerAccount"/> the bound that bites once they pay it.
    ///
    /// THE SOURCE IS THE HASH THE BFF FORWARDS, not the socket's remote address — the
    /// browser never talks to this tier, so every request here arrives from <c>web</c> and
    /// a socket-level key would limit the BFF. See <c>ClientKey</c>.
    /// </summary>
    public RateLimitPolicy LoginPerSource { get; init; } =
        new("login-source", 20, TimeSpan.FromMinutes(5), AnnouncesRetryAfter: false);

    /// <summary>
    /// Every credential-checking request against one submitted identity: 10 in 15 minutes.
    ///
    /// THE PARTITION IS THE ADDRESS THAT WAS TYPED, NOT AN ACCOUNT THAT EXISTS. That is the
    /// whole point of this policy and the reason 1.19 was pulled forward: the five-failure
    /// lockout can only count attempts it can attribute to a row, so a stream of guesses at
    /// addresses nobody has ever registered was counted by nothing whatsoever. Hashing the
    /// submitted address gives every attempt a bucket whether or not there is an account
    /// behind it — and gives both branches the same bucket SHAPE, which is what stops this
    /// policy becoming the enumeration oracle it exists to close.
    ///
    /// LOOSER THAN THE LOCKOUT ON PURPOSE. Five failures lock a real account for fifteen
    /// minutes, so for an address that does have an account the lockout is still the binding
    /// constraint and this changes nothing about Michelle's experience. Ten leaves room for
    /// the attempts the lockout deliberately does not count — a correct password followed by
    /// wrong TOTP codes — without letting an address be walked indefinitely.
    ///
    /// IT IS ALSO A DENIAL OF SERVICE ON A KNOWN ADDRESS, and that is not new. Anyone who
    /// knows Michelle's email can already lock her out for fifteen minutes with five wrong
    /// passwords; ten requests reaching a weaker version of the same state is strictly less
    /// than the control that already shipped.
    /// </summary>
    public RateLimitPolicy LoginPerAccount { get; init; } =
        new("login-account", 10, TimeSpan.FromMinutes(15), AnnouncesRetryAfter: false);

    /// <summary>
    /// Every dictation upload request from one provider: 300 in 5 minutes.
    ///
    /// DECLARED HERE BEFORE THE ENDPOINT EXISTS, and enforced by a test rather than by
    /// intent: <c>RateLimitTests.Every_expensive_route_carries_a_rate_limit</c> walks the
    /// route table and fails when a route under <c>/dictation</c> is mapped without a limit,
    /// so the first endpoint WORK_QUEUE 2.5 adds arrives red if it is not limited. A number
    /// in a class nothing enforces would be D072's defect class exactly — a control
    /// described, absent, and reading as stronger than no control at all.
    ///
    /// WHAT 2.5 HAS TO RESPECT, since these numbers constrain its design rather than the
    /// other way round:
    ///
    ///   * THE LIMITER COUNTS REQUESTS, NOT BYTES. A 9.6 MB take in 256 KB chunks is about
    ///     38 requests, so 300 carries roughly seven takes and their status polls in five
    ///     minutes — comfortably more than a session produces and far short of what a
    ///     stolen session could spend. Smaller chunks buy resumability at the cost of
    ///     budget; 256 KB is the floor these numbers assume.
    ///   * A RETRIED CHUNK SPENDS BUDGET. The uploader must back off on a 429 rather than
    ///     hot-looping, and this policy sets
    ///     <see cref="RateLimitPolicy.AnnouncesRetryAfter"/> precisely so it can.
    ///   * THE PARTITION IS THE PROVIDER, NOT THE SESSION OR THE TAKE. Keying by an
    ///     identifier the caller mints would let an attacker open a fresh bucket per upload,
    ///     which is a limiter that counts to one forever.
    ///   * THE BODY MUST NOT BE BOUND BEFORE THE FILTER RUNS. A refused chunk has to cost no
    ///     bandwidth and no blob write, so the handler takes the request stream rather than
    ///     an <c>IFormFile</c> — model binding a form reads the whole part before any filter
    ///     gets a say.
    /// </summary>
    public RateLimitPolicy DictationUpload { get; init; } =
        new("dictation-upload", 300, TimeSpan.FromMinutes(5), AnnouncesRetryAfter: true);
}
