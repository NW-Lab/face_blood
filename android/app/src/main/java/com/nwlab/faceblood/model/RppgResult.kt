package com.nwlab.faceblood.model

/**
 * Result produced by [com.nwlab.faceblood.service.RppgProcessor] after each analysis pass.
 *
 * @param bpm        Estimated heart rate in beats per minute.
 * @param snr        Signal-to-noise ratio (dB-ish).
 * @param confidence Confidence score in the range 0..1.
 * @param fps        Effective sampling rate used for the analysis (Hz).
 * @param waveform   Band-pass filtered pulse waveform, normalised to ~[-1, 1].
 * @param freqHz     Dominant frequency in Hz.
 * @param phase      Instantaneous phase of the dominant component in radians [0, 2π].
 */
data class RppgResult(
    val bpm: Float,
    val snr: Float,
    val confidence: Float,
    val fps: Float,
    val waveform: FloatArray,
    val freqHz: Float,
    val phase: Float,
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is RppgResult) return false
        return bpm == other.bpm &&
            snr == other.snr &&
            confidence == other.confidence &&
            fps == other.fps &&
            waveform.contentEquals(other.waveform) &&
            freqHz == other.freqHz &&
            phase == other.phase
    }

    override fun hashCode(): Int {
        var result = bpm.hashCode()
        result = 31 * result + snr.hashCode()
        result = 31 * result + confidence.hashCode()
        result = 31 * result + fps.hashCode()
        result = 31 * result + waveform.contentHashCode()
        result = 31 * result + freqHz.hashCode()
        result = 31 * result + phase.hashCode()
        return result
    }
}
