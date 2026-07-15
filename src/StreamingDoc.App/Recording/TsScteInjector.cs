using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace StreamingDoc.App.Recording;

/// <summary>
/// Iniezione SCTE-35 a livello di pacchetti MPEG-TS. Il muxer FFmpeg (mpegtsenc, n7.1) NON genera
/// SCTE-35, quindi avvolgiamo l'uscita con un AVIOContext custom: FFmpeg scrive i pacchetti TS da
/// 188 byte nella nostra callback, noi (1) inoltriamo tutto all'output reale, (2) patchiamo il PMT
/// aggiungendo un elementary stream SCTE-35 (stream_type 0x86 + registration descriptor "CUEI"),
/// (3) al momento del break iniettiamo la splice_info_section su un PID dedicato.
/// Tutte le scritture verso l'output reale avvengono sul thread del muxer (serializzato dal chiamante).
/// </summary>
public sealed unsafe class TsScteInjector : IDisposable
{
    public int SctePid { get; }

    private AVIOContext* _avio;   // contesto custom passato a FFmpeg (_fmt->pb)
    private AVIOContext* _real;   // output reale (file / srt://)
    private readonly avio_alloc_context_write_packet _writeDel; // tenere ref: evita GC del delegate

    private byte[] _acc = new byte[1 << 16];
    private int _accLen;
    private int _pmtPid = -1;
    private int _videoPid = -1;
    private long _lastVideoPts = -1;   // ultimo PTS video visto sul filo (90 kHz, 33 bit)
    private bool _pmtSent;
    private int _cc;
    private readonly ConcurrentQueue<SpliceReq> _pending = new();
    private readonly Dictionary<(uint, bool), long> _targetPts = new(); // pts assoluto per (eventId,out): ripetizioni identiche

    private struct SpliceReq
    {
        public byte[]? Raw;          // sezione gia' pronta (path immediato/selftest)
        public uint EventId;
        public bool Out;
        public double Dur;
        public bool Immediate;
        public double Preroll;       // secondi di anticipo (pre-roll) al momento del fire
    }

    private const int ScteEntryLen = 11; // stream header (5) + registration descriptor "CUEI" (6)

    public TsScteInjector(string url, int sctePid = 0x01F0)
    {
        SctePid = sctePid;

        AVIOContext* real;
        int r = ffmpeg.avio_open2(&real, url, ffmpeg.AVIO_FLAG_WRITE, null, null);
        if (r < 0) throw new InvalidOperationException($"avio_open2 fallito su '{url}' (codice {r}).");
        _real = real;

        int bufSize = 1 << 16;
        var buffer = (byte*)ffmpeg.av_malloc((ulong)bufSize);
        _writeDel = Write;
        var wf = new avio_alloc_context_write_packet_func { Pointer = Marshal.GetFunctionPointerForDelegate(_writeDel) };
        _avio = ffmpeg.avio_alloc_context(buffer, bufSize, write_flag: 1, opaque: null,
            read_packet: default, write_packet: wf, seek: default);
        if (_avio == null) throw new InvalidOperationException("avio_alloc_context fallito.");
    }

    /// <summary>AVIOContext custom da assegnare a <c>fmt->pb</c> (con AVFMT_FLAG_CUSTOM_IO).</summary>
    public AVIOContext* Pb => _avio;

    /// <summary>Accoda una sezione SCTE-35 gia' pronta (immediata): iniettata al prossimo TS. Thread-safe.</summary>
    public void EnqueueSplice(byte[] section)
    {
        if (section != null && section.Length is > 0 and <= 183) _pending.Enqueue(new SpliceReq { Raw = section });
    }

    /// <summary>Accoda un marker SCTE-35 parametrico: il PTS viene calcolato al momento dell'iniezione
    /// dal PTS video corrente sul filo (+ pre-roll), cosi' punta al frame giusto. Thread-safe.</summary>
    public void EnqueueScte(uint eventId, bool outOfNetwork, double durationSeconds, bool immediate, double prerollSeconds)
    {
        _pending.Enqueue(new SpliceReq { EventId = eventId, Out = outOfNetwork, Dur = durationSeconds,
            Immediate = immediate, Preroll = prerollSeconds });
    }

    // --- callback di scrittura del muxer FFmpeg ---
    private int Write(void* opaque, byte* buf, int size)
    {
        if (size <= 0) return size;
        if (_accLen + size > _acc.Length) Array.Resize(ref _acc, Math.Max(_acc.Length * 2, _accLen + size));
        Marshal.Copy((IntPtr)buf, _acc, _accLen, size);
        _accLen += size;

        int o = 0;
        while (_accLen - o >= 188)
        {
            ProcessPacket(o);
            o += 188;
        }
        int rem = _accLen - o;
        if (rem > 0 && o > 0) Array.Copy(_acc, o, _acc, 0, rem);
        _accLen = rem;
        return size;
    }

    private void ProcessPacket(int o)
    {
        byte[] a = _acc;
        if (a[o] != 0x47) { WriteRaw(a, o, 188); return; }

        bool pusi = (a[o + 1] & 0x40) != 0;
        int pid = ((a[o + 1] & 0x1F) << 8) | a[o + 2];
        int afc = (a[o + 3] >> 4) & 3;
        if ((afc & 1) == 0) { WriteRaw(a, o, 188); return; } // solo adaptation, nessun payload

        int payload = o + 4;
        if ((afc & 2) != 0) payload = o + 5 + a[o + 4]; // salta adaptation field

        if (pid == 0 && pusi && _pmtPid < 0)
            TryParsePat(a, payload + 1 + a[payload]); // dopo pointer_field

        if (_pmtPid >= 0 && pid == _pmtPid && pusi)
        {
            if (_videoPid < 0) ScanPmtVideoPid(a, payload);
            var patched = PatchPmt(a, o, payload);
            if (patched != null) { WriteRaw(patched, 0, 188); _pmtSent = true; }
            else WriteRaw(a, o, 188);
        }
        else
        {
            if (pid == _videoPid && pusi) TryParsePts(a, payload, o + 188); // aggiorna il PTS video corrente
            WriteRaw(a, o, 188);
        }

        if (_pmtSent && _pending.TryDequeue(out var req))
        {
            byte[]? section = BuildSection(req);
            if (section != null) WriteRaw(BuildSctePacket(section), 0, 188);
        }
    }

    private void TryParsePat(byte[] a, int ss)
    {
        if (a[ss] != 0x00) return; // table_id PAT
        int slen = ((a[ss + 1] & 0x0F) << 8) | a[ss + 2];
        int end = ss + 3 + slen - 4; // esclude CRC
        int p = ss + 8;             // salta header sezione fino ai program loop
        while (p + 4 <= end)
        {
            int prog = (a[p] << 8) | a[p + 1];
            int pmtPid = ((a[p + 2] & 0x1F) << 8) | a[p + 3];
            if (prog != 0) { _pmtPid = pmtPid; return; }
            p += 4;
        }
    }

    // Costruisce la sezione SCTE-35 da iniettare: raw (immediata) oppure parametrica col PTS video futuro.
    private byte[]? BuildSection(SpliceReq r)
    {
        if (r.Raw != null) return r.Raw;
        long? pts = null;
        if (!r.Immediate)
        {
            if (_lastVideoPts < 0) return null; // PTS video non ancora noto (solo a inizio stream): salta
            var key = (r.EventId, r.Out);
            if (!_targetPts.TryGetValue(key, out long tp))
            {
                // Calcola UNA volta il PTS assoluto di destinazione; le ripetizioni riusano lo stesso
                // valore -> pacchetti byte-identici (i ricevitori li deduplicano come stesso evento).
                tp = (_lastVideoPts + (long)(r.Preroll * 90000.0)) & 0x1FFFFFFFFL;
                if (_targetPts.Count > 256) _targetPts.Clear();
                _targetPts[key] = tp;
            }
            pts = tp;
        }
        // immediato -> auto_return=1 (nessun Cue-In esplicito); pre-roll -> auto_return=0 (Cue-In esplicito).
        return Scte35.BuildSpliceInsert(r.EventId, r.Out, r.Dur, pts, autoReturn: r.Immediate);
    }

    // Individua il PID del video nel PMT (H.264 0x1B, HEVC 0x24, MPEG-2 0x02) per leggerne poi il PTS.
    private void ScanPmtVideoPid(byte[] a, int payload)
    {
        int ss = payload + 1 + a[payload];       // salta pointer_field
        if (ss + 12 > _acc.Length || a[ss] != 0x02) return; // table_id PMT
        int slen = ((a[ss + 1] & 0x0F) << 8) | a[ss + 2];
        int end = ss + 3 + slen - 4;             // fine ES loop (esclude CRC)
        int piLen = ((a[ss + 10] & 0x0F) << 8) | a[ss + 11]; // program_info_length
        int p = ss + 12 + piLen;                 // inizio ES loop
        while (p + 5 <= end && p + 5 <= _acc.Length)
        {
            int stype = a[p];
            int epid = ((a[p + 1] & 0x1F) << 8) | a[p + 2];
            int esil = ((a[p + 3] & 0x0F) << 8) | a[p + 4];
            if (stype == 0x1B || stype == 0x24 || stype == 0x02) { _videoPid = epid; return; }
            p += 5 + esil;
        }
    }

    // Estrae il PTS dal PES header del pacchetto video (payload = inizio PES); aggiorna _lastVideoPts.
    private void TryParsePts(byte[] a, int payload, int limit)
    {
        if (payload + 14 > limit) return;
        if (a[payload] != 0x00 || a[payload + 1] != 0x00 || a[payload + 2] != 0x01) return; // PES start code
        if ((a[payload + 7] & 0x80) == 0) return; // PTS_DTS_flags: nessun PTS
        int q = payload + 9;
        long pts = ((long)(a[q] & 0x0E) << 29)
                 | ((long)a[q + 1] << 22)
                 | ((long)(a[q + 2] & 0xFE) << 14)
                 | ((long)a[q + 3] << 7)
                 | ((long)(a[q + 4] & 0xFE) >> 1);
        _lastVideoPts = pts & 0x1FFFFFFFFL;
    }

    // Aggiunge lo stream SCTE-35 al PMT; ritorna un pacchetto da 188 byte o null se non applicabile.
    private byte[]? PatchPmt(byte[] a, int o, int payload)
    {
        int ptr = a[payload];
        if (ptr != 0) return null;              // pointer_field non nullo: non gestito
        int ss = payload + 1;                    // inizio sezione
        if (a[ss] != 0x02) return null;          // table_id PMT
        int slen = ((a[ss + 1] & 0x0F) << 8) | a[ss + 2];
        int crcPos = ss + 3 + slen - 4;          // posizione CRC attuale
        int crcRel = crcPos - o;
        if (crcRel + ScteEntryLen + 4 > 188) return null; // non entra nel pacchetto

        var pkt = new byte[188];
        Array.Copy(a, o, pkt, 0, 188);

        int baseP = ss - o;
        int newSlen = slen + ScteEntryLen;
        pkt[baseP + 1] = (byte)((a[ss + 1] & 0xF0) | ((newSlen >> 8) & 0x0F));
        pkt[baseP + 2] = (byte)(newSlen & 0xFF);
        // incrementa version_number (byte baseP+5: reserved2|version5|current_next1)
        int ver = (pkt[baseP + 5] >> 1) & 0x1F;
        ver = (ver + 1) & 0x1F;
        pkt[baseP + 5] = (byte)((pkt[baseP + 5] & 0xC1) | (ver << 1));

        int q = crcRel;
        pkt[q++] = 0x86;                                   // stream_type SCTE-35
        pkt[q++] = (byte)(0xE0 | ((SctePid >> 8) & 0x1F)); // reserved + elementary_PID hi
        pkt[q++] = (byte)(SctePid & 0xFF);                 // elementary_PID lo
        pkt[q++] = 0xF0;                                   // reserved + ES_info_length hi (=0)
        pkt[q++] = 0x06;                                   // ES_info_length lo (registration descriptor)
        pkt[q++] = 0x05; pkt[q++] = 0x04;                  // registration_descriptor tag+len
        pkt[q++] = (byte)'C'; pkt[q++] = (byte)'U'; pkt[q++] = (byte)'E'; pkt[q++] = (byte)'I';

        uint crc = Crc32Mpeg(pkt, baseP, q - baseP);
        pkt[q++] = (byte)(crc >> 24); pkt[q++] = (byte)(crc >> 16);
        pkt[q++] = (byte)(crc >> 8); pkt[q++] = (byte)crc;
        for (int i = q; i < 188; i++) pkt[i] = 0xFF;       // stuffing
        return pkt;
    }

    private byte[] BuildSctePacket(byte[] section)
    {
        var q = new byte[188];
        q[0] = 0x47;
        q[1] = (byte)(0x40 | ((SctePid >> 8) & 0x1F)); // PUSI=1
        q[2] = (byte)(SctePid & 0xFF);
        q[3] = (byte)(0x10 | (_cc & 0x0F));            // payload-only, continuity counter
        _cc = (_cc + 1) & 0x0F;
        q[4] = 0x00;                                    // pointer_field
        Array.Copy(section, 0, q, 5, section.Length);
        for (int i = 5 + section.Length; i < 188; i++) q[i] = 0xFF;
        return q;
    }

    private void WriteRaw(byte[] data, int off, int len)
    {
        if (_real == null) return;
        fixed (byte* p = data) ffmpeg.avio_write(_real, p + off, len);
    }

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

    public void Dispose()
    {
        if (_avio != null) { try { ffmpeg.avio_flush(_avio); } catch { } }
        if (_real != null)
        {
            try { ffmpeg.avio_flush(_real); } catch { }
            AVIOContext* r = _real; ffmpeg.avio_closep(&r); _real = null;
        }
        if (_avio != null)
        {
            if (_avio->buffer != null) ffmpeg.av_freep(&_avio->buffer);
            AVIOContext* c = _avio; ffmpeg.avio_context_free(&c); _avio = null;
        }
    }
}
