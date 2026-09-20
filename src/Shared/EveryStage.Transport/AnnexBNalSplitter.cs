namespace EveryStage.Transport;

/// <summary>
/// Splits an H.264 Annex-B bytestream (the format H.264 encoders — including Media Foundation's —
/// normally emit: NAL units back to back, each preceded by a 3-byte <c>00 00 01</c> or 4-byte
/// <c>00 00 00 01</c> start code) into individual NAL units, each ready to hand to
/// <see cref="H264RtpPacketizer"/>. Pure byte scanning — no dependency on how the bytes were
/// produced, so this can be exercised today even though nothing in this repo produces real H.264
/// yet (see this library's README).
///
/// Relies on H.264's "emulation prevention" rule (Annex B): encoders must never let three
/// consecutive zero bytes occur inside a NAL unit's own payload without inserting a 0x03 byte to
/// break it up, specifically so a decoder can scan for <c>00 00 01</c> byte-for-byte without ever
/// confusing real payload data for a start code. This scanner depends on that guarantee holding for
/// its input, same as every other Annex-B parser.
/// </summary>
public static class AnnexBNalSplitter
{
    /// <summary>Each returned unit starts with the NAL header byte (forbidden_zero_bit /
    /// nal_ref_idc / nal_unit_type) and excludes the start code — this is the shape
    /// <see cref="H264RtpPacketizer"/> expects.</summary>
    public static IEnumerable<ReadOnlyMemory<byte>> Split(ReadOnlyMemory<byte> annexBBytes)
    {
        var span = annexBBytes.Span;
        var units = new List<ReadOnlyMemory<byte>>();

        // For each start code found: where its own leading zero bytes begin (codeBegin) and where
        // the NAL unit after it begins (nalStart). Advancing `i` past a matched start code before
        // continuing the scan means each byte is examined at most once — no risk of the same zero
        // bytes being read twice as two different start-code lengths.
        var codeBegins = new List<int>();
        var nalStarts = new List<int>();

        int i = 0;
        while (i + 2 < span.Length)
        {
            if (span[i] == 0 && span[i + 1] == 0)
            {
                if (span[i + 2] == 1)
                {
                    codeBegins.Add(i);
                    nalStarts.Add(i + 3);
                    i += 3;
                    continue;
                }
                if (i + 3 < span.Length && span[i + 2] == 0 && span[i + 3] == 1)
                {
                    codeBegins.Add(i);
                    nalStarts.Add(i + 4);
                    i += 4;
                    continue;
                }
            }
            i++;
        }

        for (int n = 0; n < nalStarts.Count; n++)
        {
            int nalStart = nalStarts[n];
            int nalEnd = n + 1 < codeBegins.Count ? codeBegins[n + 1] : span.Length;

            // Bug fixed here: naively ending this slice at the NEXT recognized start code's own
            // codeBegins position is wrong whenever extra zero bytes sit between this NAL's real
            // content and that next start code without themselves being recognized as part of it —
            // e.g. more than the usual 2-3 leading_zero_8bits before a start code (Annex B allows
            // an arbitrary number): the scanner's single-byte-at-a-time fallback (see the `i++`
            // above) only ever records where the start code IT ENDS UP MATCHING begins, not where
            // the run of zero bytes before it actually started, so any earlier zero byte(s) in that
            // run get silently absorbed into THIS NAL's slice as if they were genuine payload.
            // Concretely: `00 00 01 67 41 42 00 00 00 00 01 68 43 44` (one extra zero byte before
            // the second, 4-byte start code) used to return the first NAL as `67 41 42 00` instead
            // of `67 41 42`. Trimming trailing zero bytes here is spec-guaranteed safe, not a
            // heuristic: rbsp_trailing_bits (ITU-T H.264 §7.3.2.11) requires a NAL unit's RBSP to
            // end with a rbsp_stop_one_bit — a genuinely well-formed NAL's last real byte can never
            // be 0x00, so any all-zero byte(s) at the very end of a slice are always padding,
            // whether from the encoder's own trailing_zero_8bits or (as here) an artifact of this
            // scan, never content that would be wrongly stripped.
            while (nalEnd > nalStart && span[nalEnd - 1] == 0)
                nalEnd--;

            if (nalEnd > nalStart)
                units.Add(annexBBytes.Slice(nalStart, nalEnd - nalStart));
        }

        return units;
    }
}
