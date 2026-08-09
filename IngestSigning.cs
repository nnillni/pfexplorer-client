using System;
using System.Security.Cryptography;
using System.Text;

namespace PfExplorer;

// Signs every ingest request with an HMAC over (timestamp + nonce + raw
// JSON body) instead of just sending a static shared header — see
// server/src/auth.ts's requireIngestSignature for the matching
// verification (timestamp has to be within a few minutes, and each nonce
// is only ever accepted once). This is not real security: the plugin is a
// public, redistributable binary, so anyone motivated enough can pull this
// secret back out of the assembly and reimplement signing themselves. What
// it DOES meaningfully stop is casually sniffing one request and replaying
// it indefinitely — the old static x-api-key header this replaced was good
// for exactly that kind of trivial spoofing.
public static class IngestSigning
{
    // Must match the server's own INGEST_API_KEY exactly (server/src/ingestKey.ts).
    private const string Secret = "036aae57e03a8ef30e7dd8a2907ec6de3875f696da6b92349d90edaefe2d98ed";

    public static (string Timestamp, string Nonce, string Signature) Sign(string body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var nonce = Guid.NewGuid().ToString("N");
        var message = $"{timestamp}.{nonce}.{body}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        // Case doesn't matter — the server parses this back into raw bytes
        // via Buffer.from(signature, "hex") before comparing, not as text.
        var signature = Convert.ToHexString(hash);

        return (timestamp, nonce, signature);
    }

    // Keyed (not plain SHA256) so the recruiting character's real content ID
    // can't just be recovered by brute-forcing the ~64-bit ID space against
    // an unsalted hash — you'd need Secret too. The server only ever
    // treats content_id as an opaque string (equality + "xivpf-" prefix
    // checks — see routes/listings.ts), never parses it, so swapping the
    // raw ID for this deterministic hash here needs no server-side change:
    // the same character always produces the same hash, which is all the
    // "one active recruitment per character" supersede logic needs.
    public static string HashContentId(string contentId)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(contentId));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
