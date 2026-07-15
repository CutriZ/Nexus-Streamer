namespace StreamingDoc.App.Recording;

/// <summary>
/// Costruisce una <c>splice_info_section</c> SCTE-35 (comando <c>splice_insert</c>) binaria,
/// pronta per essere iniettata come pacchetto su uno stream dati AV_CODEC_ID_SCTE_35 nel muxer MPEG-TS.
/// Usiamo sempre <c>splice_immediate</c>: il cue si piazza al punto corrente dello stream (nessuna
/// mappatura di PTS/clock), <c>auto_return</c> gestisce il rientro dopo la durata del break.
/// Riferimento: ANSI/SCTE 35.
/// </summary>
public static class Scte35
{
    /// <param name="eventId">splice_event_id (identifica il break; le ripetizioni condividono lo stesso id).</param>
    /// <param name="outOfNetwork">true = inizio pubblicità (Cue-Out); false = rientro programma (Cue-In).</param>
    /// <param name="durationSeconds">durata del break in secondi; se &gt; 0 imposta break_duration (Cue-Out).</param>
    /// <param name="ptsTime90k">se valorizzato, splice temporizzato al PTS video futuro indicato (90 kHz,
    /// 33 bit) — pre-roll; se null, splice_immediate (al punto corrente dello stream).</param>
    /// <param name="autoReturn">con break_duration: 1 = rientro automatico dopo la durata (nessun Cue-In
    /// esplicito). In modalità pre-roll con Cue-In esplicito va messo a false.</param>
    public static byte[] BuildSpliceInsert(uint eventId, bool outOfNetwork, double durationSeconds,
        long? ptsTime90k = null, bool autoReturn = true)
    {
        bool hasDuration = durationSeconds > 0;
        bool immediate = ptsTime90k is null;

        var bw = new BitWriter();
        bw.Write(0, 8);              // protocol_version
        bw.Write(0, 1);             // encrypted_packet
        bw.Write(0, 6);             // encryption_algorithm
        bw.Write(0, 33);            // pts_adjustment
        bw.Write(0, 8);             // cw_index
        bw.Write(0xFFF, 12);        // tier

        // splice_command_length: lunghezza in byte dello splice_insert() qui sotto.
        int cmdLen = 4 /*event_id*/ + 1 /*cancel+res*/ + 1 /*flags*/
                     + (immediate ? 0 : 5) /*splice_time (time_specified)*/
                     + (hasDuration ? 5 : 0) /*break_duration*/
                     + 2 /*unique_program_id*/ + 1 /*avail_num*/ + 1 /*avails_expected*/;
        bw.Write((ulong)cmdLen, 12);
        bw.Write(0x05, 8);          // splice_command_type = splice_insert

        // --- splice_insert() ---
        bw.Write(eventId, 32);      // splice_event_id
        bw.Write(0, 1);             // splice_event_cancel_indicator
        bw.Write(0x7F, 7);          // reserved
        bw.Write(outOfNetwork ? 1u : 0u, 1); // out_of_network_indicator
        bw.Write(1, 1);             // program_splice_flag
        bw.Write(hasDuration ? 1u : 0u, 1);  // duration_flag
        bw.Write(immediate ? 1u : 0u, 1);    // splice_immediate_flag
        bw.Write(0xF, 4);           // reserved
        if (!immediate)
        {
            // splice_time() con program_splice=1 e immediate=0: PTS video di destinazione.
            bw.Write(1, 1);         // time_specified_flag
            bw.Write(0x3F, 6);      // reserved
            bw.Write((ulong)(ptsTime90k!.Value & 0x1FFFFFFFFL), 33); // pts_time (33 bit)
        }
        if (hasDuration)
        {
            long ticks = (long)(durationSeconds * 90000.0); // break_duration in 90 kHz
            bw.Write(autoReturn ? 1u : 0u, 1); // auto_return
            bw.Write(0x3F, 6);      // reserved
            bw.Write((ulong)(ticks & 0x1FFFFFFFFL), 33); // duration (33 bit)
        }
        bw.Write(0, 16);            // unique_program_id
        bw.Write(0, 8);             // avail_num
        bw.Write(0, 8);             // avails_expected

        bw.Write(0, 16);            // descriptor_loop_length = 0 (nessun descrittore)

        byte[] after = bw.ToBytes(); // deve risultare byte-aligned

        int sectionLen = after.Length + 4; // + CRC_32
        var section = new byte[3 + after.Length + 4];
        section[0] = 0xFC;                                             // table_id
        section[1] = (byte)(0x30 | ((sectionLen >> 8) & 0x0F));        // syntax=0, private=0, sap=11, len hi
        section[2] = (byte)(sectionLen & 0xFF);                        // len lo
        Array.Copy(after, 0, section, 3, after.Length);

        uint crc = Crc32Mpeg(section, 0, 3 + after.Length);
        int o = 3 + after.Length;
        section[o]     = (byte)(crc >> 24);
        section[o + 1] = (byte)(crc >> 16);
        section[o + 2] = (byte)(crc >> 8);
        section[o + 3] = (byte)crc;
        return section;
    }

    /// <summary>CRC-32/MPEG-2 (poly 0x04C11DB7, init 0xFFFFFFFF, MSB-first) come da sezioni PSI MPEG.</summary>
    private static uint Crc32Mpeg(byte[] data, int off, int len)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < len; i++)
        {
            crc ^= (uint)data[off + i] << 24;
            for (int b = 0; b < 8; b++)
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }
        return crc;
    }

    /// <summary>Accumulatore di bit MSB-first; garantisce l'allineamento a byte in uscita.</summary>
    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = new();
        private ulong _acc;
        private int _bits;

        public void Write(ulong val, int n)
        {
            ulong mask = n >= 64 ? ulong.MaxValue : (1UL << n) - 1;
            _acc = (_acc << n) | (val & mask);
            _bits += n;
            while (_bits >= 8)
            {
                _bits -= 8;
                _bytes.Add((byte)((_acc >> _bits) & 0xFF));
            }
        }

        public byte[] ToBytes()
        {
            if (_bits != 0) throw new InvalidOperationException("SCTE-35: sezione non byte-aligned.");
            return _bytes.ToArray();
        }
    }
}
