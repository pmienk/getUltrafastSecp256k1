// libbitcoin ECDSA drop-in vector check against the UltrafastSecp256k1 shim.
// Mirrors libbitcoin-system test/crypto/secp256k1.cpp exactly:
//   #1 sign(secret3, sighash3) must byte-equal signature3 (raw opaque struct).
//   #2 serialize_der(pun(signature3)) must equal der_signature3.
#include <secp256k1.h>
#include <cstdio>
#include <cstring>
#include <cstdint>

static bool unhex(const char* h, unsigned char* out, size_t n) {
    auto nib = [](char c) -> int {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    };
    for (size_t i = 0; i < n; ++i) {
        int hi = nib(h[2*i]), lo = nib(h[2*i+1]);
        if (hi < 0 || lo < 0) return false;
        out[i] = (unsigned char)((hi << 4) | lo);
    }
    return true;
}

static void dump(const char* tag, const unsigned char* p, size_t n) {
    printf("  %-14s ", tag);
    for (size_t i = 0; i < n; ++i) printf("%02x", p[i]);
    printf("\n");
}

int main() {
    // libbitcoin constants (test/crypto/secp256k1.cpp)
    const char* SECRET3 = "33436393f770d9b3f5d11c20be561837300f89515284008965d2fd3f714b8fce";
    const char* SIGHASH3 = "f89572635651b2e4f89778350616989183c98d1a721c911324bf9f17a0cf5bf0";
    const char* SIG3 = "4832febef8b31c7c922a15cb4063a43ab69b099bba765e24facef50dfbb4d057928ed5c6b6886562c2fe6972fd7c7f462e557129067542cce6b37d72e5ea5037";
    const char* DER3 = "3044022057d0b4fb0df5cefa245e76ba9b099bb63aa46340cb152a927c1cb3f8befe324802203750eae5727db3e6cc4275062971552e467f7cfd7269fec2626588b6c6d58e92";

    unsigned char secret3[32], sighash3[32], signature3[64], der3[72];
    unhex(SECRET3, secret3, 32);
    unhex(SIGHASH3, sighash3, 32);
    // libbitcoin's sighash3 uses base16_hash(), which REVERSES the bytes
    // (Bitcoin hashes display big-endian, store little-endian). The message
    // actually passed to secp256k1_ecdsa_sign is the byte-reversed literal.
    for (int i = 0; i < 16; ++i) { unsigned char t = sighash3[i]; sighash3[i] = sighash3[31-i]; sighash3[31-i] = t; }
    unhex(SIG3, signature3, 64);
    size_t der3_len = strlen(DER3) / 2;
    unhex(DER3, der3, der3_len);

    secp256k1_context* ctx =
        secp256k1_context_create(SECP256K1_CONTEXT_SIGN | SECP256K1_CONTEXT_VERIFY);

    int fails = 0;

    // -- Test #1: sign --------------------------------------------------------
    secp256k1_ecdsa_signature sig;
    int ok = secp256k1_ecdsa_sign(ctx, &sig, sighash3, secret3,
                                  secp256k1_nonce_function_rfc6979, nullptr);
    printf("Test #1 sign:\n");
    if (!ok) { printf("  FAIL: secp256k1_ecdsa_sign returned 0\n"); ++fails; }
    else if (memcmp(sig.data, signature3, 64) != 0) {
        printf("  FAIL: signature != signature3\n");
        dump("got", sig.data, 64);
        dump("want", signature3, 64);
        ++fails;
    } else {
        printf("  PASS\n");
    }

    // -- Diagnostics: is the message/secret right, or is it purely the nonce? -
    secp256k1_pubkey pub;
    int pok = secp256k1_ec_pubkey_create(ctx, &pub, secret3);
    printf("Diagnostics:\n");
    printf("  pubkey_create:            %d\n", pok);
    secp256k1_ecdsa_signature s3;
    memcpy(s3.data, signature3, 64);   // signature3 in opaque/internal layout
    printf("  verify(signature3,sighash3)=%d  (1 => message+secret correct)\n",
           secp256k1_ecdsa_verify(ctx, &s3, sighash3, &pub));
    if (ok) printf("  verify(produced,  sighash3)=%d  (1 => valid sig, nonce differs)\n",
           secp256k1_ecdsa_verify(ctx, &sig, sighash3, &pub));

    // -- Test #2: encode_signature (pun + serialize_der) ----------------------
    secp256k1_ecdsa_signature sig2;
    memcpy(sig2.data, signature3, 64);          // libbitcoin pointer_cast, no parse
    unsigned char der[72];
    size_t derlen = sizeof(der);
    int ok2 = secp256k1_ecdsa_signature_serialize_der(ctx, der, &derlen, &sig2);
    printf("Test #2 encode_signature:\n");
    if (!ok2) { printf("  FAIL: serialize_der returned 0\n"); ++fails; }
    else if (derlen != der3_len || memcmp(der, der3, derlen) != 0) {
        printf("  FAIL: der != der_signature3\n");
        dump("got", der, derlen);
        dump("want", der3, der3_len);
        ++fails;
    } else {
        printf("  PASS\n");
    }

    secp256k1_context_destroy(ctx);
    printf("\n%s (%d failure%s)\n", fails ? "RESULT: FAIL" : "RESULT: PASS",
           fails, fails == 1 ? "" : "s");
    return fails ? 1 : 0;
}
