using System;
using System.Buffers.Binary;
using System.Numerics;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// SHA-256 (FIPS 180-4) in plain code, for the install's instance key
/// (<see cref="AppStoragePaths.GetInstallInstanceKey"/>), which every start works out before anything else: .NET's own
/// <c>SHA256</c> lives in System.Security.Cryptography.dll, a 2.4 MB file nothing else in a start uses, so it was among
/// those the launcher reads at sign-in, for a hash of a 30-character path. The same digest as <c>SHA256.HashData</c>
/// (Sha256DigestTests); not for anything secret, nor for large inputs: it copies the message once to pad it.
/// </summary>
internal static class Sha256Digest
{
    private static readonly uint[] RoundConstants =
    [
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
    ];

    /// <summary>The 32-byte SHA-256 digest of <paramref name="message"/>.</summary>
    internal static byte[] HashData(ReadOnlySpan<byte> message)
    {
        // The message, a 1 bit, zeros, then its length in bits as a big-endian 64-bit number, to whole 64-byte blocks.
        int paddedLength = (message.Length + 1 + sizeof(ulong) + 63) / 64 * 64;
        byte[] padded = new byte[paddedLength];
        message.CopyTo(padded);
        padded[message.Length] = 0x80;
        BinaryPrimitives.WriteUInt64BigEndian(padded.AsSpan(paddedLength - sizeof(ulong)), (ulong)message.Length * 8);

        Span<uint> state =
        [
            0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19,
        ];
        Span<uint> schedule = stackalloc uint[64];
        for (int block = 0; block < paddedLength; block += 64)
        {
            for (int i = 0; i < 16; i++)
            {
                schedule[i] = BinaryPrimitives.ReadUInt32BigEndian(padded.AsSpan(block + (i * 4), 4));
            }

            for (int i = 16; i < 64; i++)
            {
                uint w15 = schedule[i - 15];
                uint w2 = schedule[i - 2];
                uint s0 = BitOperations.RotateRight(w15, 7) ^ BitOperations.RotateRight(w15, 18) ^ (w15 >> 3);
                uint s1 = BitOperations.RotateRight(w2, 17) ^ BitOperations.RotateRight(w2, 19) ^ (w2 >> 10);
                schedule[i] = schedule[i - 16] + s0 + schedule[i - 7] + s1;
            }

            uint a = state[0], b = state[1], c = state[2], d = state[3];
            uint e = state[4], f = state[5], g = state[6], h = state[7];
            for (int i = 0; i < 64; i++)
            {
                uint sum1 = BitOperations.RotateRight(e, 6) ^ BitOperations.RotateRight(e, 11)
                    ^ BitOperations.RotateRight(e, 25);
                uint choice = (e & f) ^ (~e & g);
                uint temp1 = h + sum1 + choice + RoundConstants[i] + schedule[i];
                uint sum0 = BitOperations.RotateRight(a, 2) ^ BitOperations.RotateRight(a, 13)
                    ^ BitOperations.RotateRight(a, 22);
                uint majority = (a & b) ^ (a & c) ^ (b & c);
                uint temp2 = sum0 + majority;
                h = g;
                g = f;
                f = e;
                e = d + temp1;
                d = c;
                c = b;
                b = a;
                a = temp1 + temp2;
            }

            state[0] += a;
            state[1] += b;
            state[2] += c;
            state[3] += d;
            state[4] += e;
            state[5] += f;
            state[6] += g;
            state[7] += h;
        }

        byte[] digest = new byte[32];
        for (int i = 0; i < state.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(digest.AsSpan(i * 4, 4), state[i]);
        }

        return digest;
    }
}
