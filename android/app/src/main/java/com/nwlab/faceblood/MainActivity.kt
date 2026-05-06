package com.nwlab.faceblood

import android.Manifest
import android.content.pm.PackageManager
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.core.content.ContextCompat
import com.nwlab.faceblood.service.MeasurePhase
import com.nwlab.faceblood.service.PulseViewModel
import com.nwlab.faceblood.ui.MainScreen
import com.nwlab.faceblood.ui.theme.FaceBloodTheme

/**
 * Single-activity entry point.
 *
 * Requests the CAMERA permission and then delegates all UI and logic to
 * [PulseViewModel] + [MainScreen].
 */
class MainActivity : ComponentActivity() {

    private val viewModel: PulseViewModel by viewModels()

    private val permissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
            if (granted) {
                viewModel.start(this)
            } else {
                // ViewModel will surface the error through its StateFlow.
                viewModel.onPermissionDenied()
            }
        }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        setContent {
            FaceBloodTheme {
                val phase by viewModel.phase.collectAsState()
                val bpm by viewModel.bpm.collectAsState()
                val snr by viewModel.snr.collectAsState()
                val confidence by viewModel.confidence.collectAsState()
                val fps by viewModel.fps.collectAsState()
                val sampleCount by viewModel.sampleCount.collectAsState()
                val waveform by viewModel.waveform.collectAsState()
                val processedBitmap by viewModel.processedBitmap.collectAsState()
                val beatPulse by viewModel.beatPulse.collectAsState()
                val errorMessage by viewModel.errorMessage.collectAsState()
                val modeId by viewModel.modeId.collectAsState()
                val amp by viewModel.amp.collectAsState()

                MainScreen(
                    phase = phase,
                    bpm = bpm,
                    snr = snr,
                    confidence = confidence,
                    fps = fps,
                    sampleCount = sampleCount,
                    waveform = waveform,
                    processedBitmap = processedBitmap,
                    beatPulse = beatPulse,
                    errorMessage = errorMessage,
                    modeId = modeId,
                    amp = amp,
                    onStart = { requestCameraAndStart() },
                    onStop = { viewModel.stop() },
                    onModeChange = { viewModel.setMode(it) },
                    onAmpChange = { viewModel.setAmp(it) },
                    modifier = Modifier.fillMaxSize(),
                )
            }
        }
    }

    private fun requestCameraAndStart() {
        when {
            ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA)
                == PackageManager.PERMISSION_GRANTED -> {
                viewModel.start(this)
            }
            else -> {
                permissionLauncher.launch(Manifest.permission.CAMERA)
            }
        }
    }
}
