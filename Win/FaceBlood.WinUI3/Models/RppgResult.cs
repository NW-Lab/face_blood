namespace FaceBlood_WinUI3.Models
{
    public sealed class RppgResult
    {
        public double Bpm { get; set; }

        public double Snr { get; set; }

        public double Confidence { get; set; }

        public double Fps { get; set; }

        public double FreqHz { get; set; }

        public double Phase { get; set; }

        public IReadOnlyList<double> Waveform { get; set; }
    }
}
