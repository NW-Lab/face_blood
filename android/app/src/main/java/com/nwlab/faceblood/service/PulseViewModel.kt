package com.nwlab.faceblood.service

import android.app.Application
import android.graphics.Bitmap
import android.graphics.ImageFormat
import android.util.Log
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageProxy
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.core.content.ContextCompat
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.viewModelScope
import com.nwlab.faceblood.model.PulseMode
import com.nwlab.faceblood.model.PulseModeId
import com.nwlab.faceblood.model.RgbSample
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.nio.ByteBuffer
import java.util.concurrent.Executors
import kotlin.math.abs
import kotlin.math.exp
import kotlin.math.min
import kotlin.math.sqrt

private const val TAG = "PulseViewModel"

/** Phase of the measurement lifecycle. */
enum class MeasurePhase { IDLE, CALIBRATING, MEASURING, ERROR }

/**
 * ViewModel that owns the CameraX pipeline, the [RppgProcessor], and the
 * [PulseAmplifier], and exposes all HUD state as [StateFlow]s for Compose.
 *
 * Architecture mirrors [PulseEngine] on iOS.
 */
class PulseViewModel(application: Application) : AndroidViewModel(application) {

    // -------------------------------------------------------------------------
    // HUD state
    // -------------------------------------------------------------------------

    private val _phase = MutableStateFlow(MeasurePhase.IDLE)
    val phase: StateFlow<MeasurePhase> = _phase.asStateFlow()

    private val _bpm = MutableStateFlow(0f)
    val bpm: StateFlow<Float> = _bpm.asStateFlow()

    private val _snr = MutableStateFlow(0f)
    val snr: StateFlow<Float> = _snr.asStateFlow()

    private val _confidence = MutableStateFlow(0f)
    val confidence: StateFlow<Float> = _confidence.asStateFlow()

    private val _fps = MutableStateFlow(0f)
    val fps: StateFlow<Float> = _fps.asStateFlow()

    private val _sampleCount = MutableStateFlow(0)
    val sampleCount: StateFlow<Int> = _sampleCount.asStateFlow()

    private val _waveform = MutableStateFlow<List<Float>>(emptyList())
    val waveform: StateFlow<List<Float>> = _waveform.asStateFlow()

    private val _processedBitmap = MutableStateFlow<Bitmap?>(null)
    val processedBitmap: StateFlow<Bitmap?> = _processedBitmap.asStateFlow()

    private val _beatPulse = MutableStateFlow(false)
    val beatPulse: StateFlow<Boolean> = _beatPulse.asStateFlow()

    private val _errorMessage = MutableStateFlow("")
    val errorMessage: StateFlow<String> = _errorMessage.asStateFlow()

    // -------------------------------------------------------------------------
    // User-controlled state
    // -------------------------------------------------------------------------

    private val _modeId = MutableStateFlow(PulseModeId.VIVID)
    val modeId: StateFlow<PulseModeId> = _modeId.asStateFlow()

    private val _amp = MutableStateFlow(5.0f)
    val amp: StateFlow<Float> = _amp.asStateFlow()

    fun setMode(id: PulseModeId) { _modeId.value = id }
    fun setAmp(v: Float) { _amp.value = v }

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------

    private val processor = RppgProcessor(windowSeconds = 12.0)
    private val amplifier = PulseAmplifier()
    private val analysisExecutor = Executors.newSingleThreadExecutor()

    private var smoothPulse = 0f
    private var lastBeatPhase = 0f
    private var lastFrameNs = 0L
    private var beatResetJob: Job? = null

    private var cameraProvider: ProcessCameraProvider? = null

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    fun start(lifecycleOwner: LifecycleOwner) {
        if (_phase.value != MeasurePhase.IDLE && _phase.value != MeasurePhase.ERROR) return
        processor.reset()
        smoothPulse = 0f
        lastBeatPhase = 0f
        lastFrameNs = 0L
        _phase.value = MeasurePhase.CALIBRATING
        _errorMessage.value = ""

        val ctx = getApplication<Application>()
        val future = ProcessCameraProvider.getInstance(ctx)
        future.addListener({
            try {
                val provider = future.get()
                cameraProvider = provider
                bindCamera(provider, lifecycleOwner)
            } catch (e: Exception) {
                Log.e(TAG, "CameraProvider failed", e)
                _phase.value = MeasurePhase.ERROR
                _errorMessage.value = "カメラを起動できませんでした: ${e.localizedMessage}"
            }
        }, ContextCompat.getMainExecutor(ctx))
    }

    fun onPermissionDenied() {
        _phase.value = MeasurePhase.ERROR
        _errorMessage.value = "カメラへのアクセスが許可されていません。設定アプリから許可してください。"
    }

    fun stop() {
        cameraProvider?.unbindAll()
        processor.reset()
        _phase.value = MeasurePhase.IDLE
        _bpm.value = 0f
        _snr.value = 0f
        _confidence.value = 0f
        _fps.value = 0f
        _sampleCount.value = 0
        _waveform.value = emptyList()
        _processedBitmap.value = null
        smoothPulse = 0f
        beatResetJob?.cancel()
        _beatPulse.value = false
    }

    override fun onCleared() {
        super.onCleared()
        analysisExecutor.shutdown()
    }

    // -------------------------------------------------------------------------
    // Camera binding
    // -------------------------------------------------------------------------

    private fun bindCamera(provider: ProcessCameraProvider, owner: LifecycleOwner) {
        val selector = CameraSelector.Builder()
            .requireLensFacing(CameraSelector.LENS_FACING_FRONT)
            .build()

        val analysis = ImageAnalysis.Builder()
            .setTargetResolution(android.util.Size(640, 480))
            .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
            .setOutputImageFormat(ImageAnalysis.OUTPUT_IMAGE_FORMAT_RGBA_8888)
            .build()
            .also { it.setAnalyzer(analysisExecutor, ::analyzeFrame) }

        provider.unbindAll()
        try {
            provider.bindToLifecycle(owner, selector, analysis)
        } catch (e: Exception) {
            Log.e(TAG, "Camera bind failed", e)
            _phase.value = MeasurePhase.ERROR
            _errorMessage.value = "カメラのバインドに失敗しました: ${e.localizedMessage}"
        }
    }

    // -------------------------------------------------------------------------
    // Frame analysis
    // -------------------------------------------------------------------------

    private fun analyzeFrame(proxy: ImageProxy) {
        try {
            val nowNs = System.nanoTime()
            val timestampS = nowNs / 1_000_000_000.0

            // Extract RGBA_8888 plane
            val plane = proxy.planes[0]
            val buffer: ByteBuffer = plane.buffer
            val rowStride = plane.rowStride
            val pixelStride = plane.pixelStride
            val w = proxy.width
            val h = proxy.height

            // Compute mean RGB in the central square ROI (55% of shorter side)
            val shorter = min(w, h)
            val roiSize = (shorter * 0.55f).toInt()
            val rx = (w - roiSize) / 2
            val ry = (h - roiSize) / 2
            val stride = 4   // sample every 4th pixel

            var sumR = 0L; var sumG = 0L; var sumB = 0L; var count = 0L
            for (y in ry until ry + roiSize step stride) {
                for (x in rx until rx + roiSize step stride) {
                    val idx = y * rowStride + x * pixelStride
                    sumR += (buffer[idx].toInt() and 0xFF)
                    sumG += (buffer[idx + 1].toInt() and 0xFF)
                    sumB += (buffer[idx + 2].toInt() and 0xFF)
                    count++
                }
            }
            if (count == 0L) return

            val sample = RgbSample(
                t = timestampS,
                r = sumR.toFloat() / count,
                g = sumG.toFloat() / count,
                b = sumB.toFloat() / count,
            )

            // Build a Bitmap for the amplifier (RGBA → ARGB_8888)
            val bitmap = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888)
            bitmap.copyPixelsFromBuffer(buffer.rewind())

            // Post to main thread for state updates
            viewModelScope.launch(Dispatchers.Main) {
                handleFrame(bitmap, sample, timestampS, nowNs)
            }
        } finally {
            proxy.close()
        }
    }

    // -------------------------------------------------------------------------
    // Main frame handler (runs on Main dispatcher)
    // -------------------------------------------------------------------------

    private suspend fun handleFrame(
        rawBitmap: Bitmap,
        sample: RgbSample,
        timestampS: Double,
        nowNs: Long,
    ) {
        processor.push(sample)
        _sampleCount.value = processor.count

        val res = processor.analyze()
        var pulseRaw = 0f

        if (res != null) {
            _bpm.value = res.bpm
            _snr.value = res.snr
            _confidence.value = res.confidence
            _fps.value = res.fps
            pulseRaw = res.waveform.lastOrNull() ?: 0f

            // Down-sample waveform to ~200 points
            val wfStride = maxOf(1, res.waveform.size / 200)
            val pts = mutableListOf<Float>()
            var i = 0
            while (i < res.waveform.size) { pts.add(res.waveform[i]); i += wfStride }
            _waveform.value = pts

            // Beat detection — fire vignette when phase wraps
            val ph = res.phase
            val prev = lastBeatPhase
            val wrapped = ph < prev - 1.0f || (prev > 5f && ph < 1f)
            if (wrapped && res.confidence > 0.2f && !_beatPulse.value) {
                _beatPulse.value = true
                beatResetJob?.cancel()
                beatResetJob = viewModelScope.launch {
                    delay(320)
                    _beatPulse.value = false
                }
            }
            lastBeatPhase = ph

            if (_phase.value == MeasurePhase.CALIBRATING) {
                _phase.value = MeasurePhase.MEASURING
            }
        }

        // Smooth the pulse with EMA
        val dt = if (lastFrameNs > 0L) (nowNs - lastFrameNs) / 1_000_000_000f else 1f / 30f
        lastFrameNs = nowNs
        val tau = 0.06f
        val alpha = 1f - exp(-dt / tau)
        smoothPulse += (pulseRaw - smoothPulse) * alpha

        // GPU colour amplification (off main thread)
        val mode = PulseMode.by(_modeId.value)
        val ampVal = _amp.value
        val conf = _confidence.value
        val sp = smoothPulse
        withContext(Dispatchers.Default) {
            val processed = amplifier.process(rawBitmap, mode, sp, ampVal, conf)
            withContext(Dispatchers.Main) {
                _processedBitmap.value = processed
            }
        }
    }
}
