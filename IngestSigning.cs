using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PfExplorer;

// Signs every ingest request with an HMAC over (timestamp + nonce + raw
// JSON body) instead of just sending a static shared header — see
// server/src/auth.ts's requireIngestSignature for the matching
// verification (timestamp has to be within a few minutes, and each nonce
// is only ever accepted once). This is obfuscation, not real security: the
// plugin is a public, redistributable binary, so a sufficiently motivated
// person can always pull the derived secret back out (see below) and
// reimplement signing themselves. What it DOES meaningfully stop is
// casually sniffing one request and replaying it indefinitely — the old
// static x-api-key header this replaced was good for exactly that kind of
// trivial spoofing.
public static class IngestSigning
{
    // The shared secret isn't a literal string in source — it's derived
    // from one of the bundled category icons (icons/roleplay.png, already
    // shipped for the "Other"/no-category match rows, so this adds no new
    // asset) by slicing out an arbitrary byte range, XOR-scrambling it with
    // a position-dependent pattern, then hashing that. Must match the
    // server's own INGEST_API_KEY exactly, which was generated once by
    // running this same derivation and copying the result in — these are
    // separate build artifacts (plugin binary vs. server deployment), so
    // keeping them in sync is a manual step whenever icons/roleplay.png (or
    // this derivation) changes, not something automated across the repos.
    private const int SliceOffset = 256;
    private const int SliceLength = 256;

    private static readonly Lazy<string> DerivedSecret = new(DeriveSecret);

    private static string DeriveSecret()
    {
        var iconsDir = Path.Combine(Plugin.PluginInterface.AssemblyLocation.DirectoryName ?? ".", "icons");
        var path = Path.Combine(iconsDir, "roleplay.png");
        var fileBytes = File.ReadAllBytes(path);

        var slice = new byte[SliceLength];
        Array.Copy(fileBytes, SliceOffset, slice, 0, SliceLength);
        for (var i = 0; i < slice.Length; i++)
            slice[i] ^= (byte)(i * 7 + 13);

        var hash = SHA256.HashData(slice);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static (string Timestamp, string Nonce, string Signature) Sign(string body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var nonce = Guid.NewGuid().ToString("N");
        var message = $"{timestamp}.{nonce}.{body}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(DerivedSecret.Value));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        // Case doesn't matter — the server parses this back into raw bytes
        // via Buffer.from(signature, "hex") before comparing, not as text.
        var signature = Convert.ToHexString(hash);

        return (timestamp, nonce, signature);
    }

    // Keyed (not plain SHA256) so the recruiting character's real content ID
    // can't just be recovered by brute-forcing the ~64-bit ID space against
    // an unsalted hash — you'd need DerivedSecret too. The server only ever
    // treats content_id as an opaque string (equality + "xivpf-" prefix
    // checks — see routes/listings.ts), never parses it, so swapping the
    // raw ID for this deterministic hash here needs no server-side change:
    // the same character always produces the same hash, which is all the
    // "one active recruitment per character" supersede logic needs.
    public static string HashContentId(string contentId)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(DerivedSecret.Value));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(contentId));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
